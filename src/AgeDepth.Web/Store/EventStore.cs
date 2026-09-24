using System.Text.Json;
using AgeDepth.Domain;
using AgeDepth.Engine;
using Microsoft.Data.Sqlite;

namespace AgeDepth.Store;

/// <summary>Distinct failure outcomes; resource, format and semantic failures are never conflated.</summary>
public sealed class FormatCorruptException(string message) : Exception(message);

public sealed record StoredEvent(long Seq, string Id, string Type, string Payload, string At);

/// <summary>
/// Append-only event store on SQLite. Events are written in batches; a batch is visible
/// only after its commit marker lands, so a crash mid-write is rolled back by Recover().
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _db;

    public EventStore(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS batch(id TEXT PRIMARY KEY, committed INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS event(
              seq INTEGER PRIMARY KEY,
              id TEXT NOT NULL UNIQUE,
              batch_id TEXT NOT NULL,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS derived(
              node_key TEXT NOT NULL,
              input_digest TEXT NOT NULL,
              param_version TEXT NOT NULL,
              result_json TEXT NOT NULL,
              event_id TEXT NOT NULL,
              PRIMARY KEY(node_key, input_digest));
            INSERT OR IGNORE INTO meta(key, value) VALUES('modelset', '0');
            """;
        cmd.ExecuteNonQuery();
        Recover();
    }

    /// <summary>Drop events whose batch never committed (half-written writes).</summary>
    public void Recover()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM event WHERE batch_id IN (SELECT id FROM batch WHERE committed = 0);
            DELETE FROM batch WHERE committed = 0;
            """;
        cmd.ExecuteNonQuery();
    }

    public long AppendBatch(IReadOnlyList<(string id, string type, string payload)> events)
    {
        using var tx = _db.BeginTransaction();
        var batchId = Guid.NewGuid().ToString("N");
        Exec(tx, "INSERT INTO batch(id, committed) VALUES($b, 0)", ("$b", batchId));
        long last = 0;
        foreach (var e in events)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO event(id, batch_id, type, payload, at) VALUES($i,$b,$t,$p,$a) RETURNING seq";
            cmd.Parameters.AddWithValue("$i", e.id);
            cmd.Parameters.AddWithValue("$b", batchId);
            cmd.Parameters.AddWithValue("$t", e.type);
            cmd.Parameters.AddWithValue("$p", e.payload);
            cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("o"));
            last = (long)(cmd.ExecuteScalar() ?? 0L);
        }
        Exec(tx, "UPDATE batch SET committed = 1 WHERE id = $b", ("$b", batchId));
        tx.Commit();
        return last;
    }

    public List<StoredEvent> ReadEvents(long? upToSeq = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq, id, type, payload, at FROM event ORDER BY seq"
            : "SELECT seq, id, type, payload, at FROM event WHERE seq <= $s ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$s", upToSeq.Value);
        var list = new List<StoredEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public long ModelSetVersion =>
        (long)(ExecScalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key='modelset'") ?? 0L);

    /// <summary>Optimistic concurrency: exactly one of two concurrent freezes wins.</summary>
    public bool TryAdvanceModelSet(long expected)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE meta SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key='modelset' AND CAST(value AS INTEGER) = $e";
        cmd.Parameters.AddWithValue("$e", expected);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void BumpModelSet() =>
        Exec(null, "UPDATE meta SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key='modelset'");

    public void UpsertDerived(string nodeKey, string digest, string paramVersion, string resultJson, string eventId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO derived(node_key, input_digest, param_version, result_json, event_id)
            VALUES($k, $d, $p, $r, $e)
            ON CONFLICT(node_key, input_digest) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$k", nodeKey);
        cmd.Parameters.AddWithValue("$d", digest);
        cmd.Parameters.AddWithValue("$p", paramVersion);
        cmd.Parameters.AddWithValue("$r", resultJson);
        cmd.Parameters.AddWithValue("$e", eventId);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, (string digest, string eventId)> DerivedIndex()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT node_key, input_digest, event_id FROM derived ORDER BY node_key";
        var map = new Dictionary<string, (string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[$"{r.GetString(0)}|{r.GetString(1)}"] = (r.GetString(1), r.GetString(2));
        return map;
    }

    public string Export()
    {
        var events = ReadEvents();
        var doc = new
        {
            format = "agedepth-export",
            version = 1,
            modelSetVersion = ModelSetVersion,
            events = events.Select(e => new { e.Seq, e.Id, e.Type, payload = JsonDocument.Parse(e.Payload).RootElement, e.At })
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Wipe and re-import. Identities, order and provenance are preserved exactly.</summary>
    public void Import(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatCorruptException($"Export file is not valid JSON: {ex.Message}"); }
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var f) || f.GetString() != "agedepth-export")
            throw new FormatCorruptException("Missing or unknown export format marker.");
        if (!root.TryGetProperty("events", out var evs))
            throw new FormatCorruptException("Export file has no 'events' array.");

        using var tx = _db.BeginTransaction();
        Exec(tx, "DELETE FROM event; DELETE FROM batch; DELETE FROM derived;");
        var batchId = Guid.NewGuid().ToString("N");
        Exec(tx, "INSERT INTO batch(id, committed) VALUES($b, 0)", ("$b", batchId));
        foreach (var e in evs.EnumerateArray())
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO event(seq, id, batch_id, type, payload, at) VALUES($s,$i,$b,$t,$p,$a)";
            cmd.Parameters.AddWithValue("$s", e.GetProperty("Seq").GetInt64());
            cmd.Parameters.AddWithValue("$i", e.GetProperty("Id").GetString()!);
            cmd.Parameters.AddWithValue("$b", batchId);
            cmd.Parameters.AddWithValue("$t", e.GetProperty("Type").GetString()!);
            cmd.Parameters.AddWithValue("$p", e.GetProperty("payload").GetRawText());
            cmd.Parameters.AddWithValue("$a", e.GetProperty("At").GetString()!);
            cmd.ExecuteNonQuery();
        }
        Exec(tx, "UPDATE batch SET committed = 1 WHERE id = $b", ("$b", batchId));
        if (root.TryGetProperty("modelSetVersion", out var v))
            Exec(tx, "UPDATE meta SET value = $v WHERE key='modelset'", ("$v", v.GetInt64().ToString()));
        tx.Commit();
    }

    private void Exec(SqliteTransaction? tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? ExecScalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _db.Dispose();
}
