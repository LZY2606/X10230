using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeDepth;

/// <summary>
/// Append-only event store over SQLite. Immutable materials and mutable decisions
/// live in separate layers; every computation records input hash, parameter
/// version and parent dependencies. Export/import preserves identity, order and
/// provenance; a corrupt or half-written import is rejected atomically.
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;

    public Store(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS materials(
              id TEXT PRIMARY KEY, type TEXT NOT NULL, hash TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL, kind TEXT NOT NULL,
              at TEXT NOT NULL, parents TEXT NOT NULL, param_version TEXT NOT NULL,
              input_hash TEXT NOT NULL, payload TEXT NOT NULL, status TEXT NOT NULL,
              checksum TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    public long HeadSeq()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public string? Meta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void PutMaterial(Material m, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO materials(id,type,hash,json) VALUES($i,$t,$h,$j)";
        cmd.Parameters.AddWithValue("$i", m.Id);
        cmd.Parameters.AddWithValue("$t", m.Type);
        cmd.Parameters.AddWithValue("$h", m.Hash);
        cmd.Parameters.AddWithValue("$j", m.Json);
        cmd.ExecuteNonQuery();
    }

    public List<Material> Materials()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,type,hash,json FROM materials ORDER BY rowid";
        var list = new List<Material>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new Material(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    private static string EventChecksum(string id, string kind, string payload) =>
        Hashing.Sha256(id + "|" + kind + "|" + payload);

    public EventRecord AppendEvent(string kind, string[] parents, string paramVersion,
        string inputHash, string payload, string status, string? forcedId = null,
        string? forcedAt = null, SqliteTransaction? tx = null)
    {
        var id = forcedId ?? "ev-" + Hashing.Sha256(Guid.NewGuid().ToString());
        var at = forcedAt ?? DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events(id,kind,at,parents,param_version,input_hash,payload,status,checksum)
            VALUES($i,$k,$a,$p,$pv,$ih,$pl,$s,$c)
            """;
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$a", at);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(parents));
        cmd.Parameters.AddWithValue("$pv", paramVersion);
        cmd.Parameters.AddWithValue("$ih", inputHash);
        cmd.Parameters.AddWithValue("$pl", payload);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$c", EventChecksum(id, kind, payload));
        cmd.ExecuteNonQuery();
        return Events().First(e => e.Id == id);
    }

    public List<EventRecord> Events(long? upToSeq = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,kind,at,parents,param_version,input_hash,payload,status FROM events ORDER BY seq"
            : "SELECT seq,id,kind,at,parents,param_version,input_hash,payload,status FROM events WHERE seq<=$s ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$s", upToSeq.Value);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? Array.Empty<string>(),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));
        return list;
    }

    /// <summary>
    /// Optimistic-concurrency freeze: only succeeds when the caller's view of the
    /// head is current. A concurrent freeze moves the head; the loser gets a
    /// Conflict event, so both attempts remain in the record.
    /// </summary>
    public (ResultKind Kind, EventRecord? Event) Freeze(long expectedHeadSeq, string modelPayload)
    {
        using var tx = _db.BeginTransaction();
        long head;
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            head = (long)(cmd.ExecuteScalar() ?? 0L);
        }
        if (head != expectedHeadSeq)
        {
            var conflict = AppendEvent("freeze", Array.Empty<string>(), "params/v1",
                Hashing.Sha256(modelPayload), modelPayload, "conflict", tx: tx);
            tx.Commit();
            return (ResultKind.Conflict, conflict);
        }
        var ev = AppendEvent("freeze", Array.Empty<string>(), "params/v1",
            Hashing.Sha256(modelPayload), modelPayload, "frozen", tx: tx);
        SetMeta("frozenHead", ev.Seq.ToString(), tx);
        SetMeta("frozenModel", modelPayload, tx);
        tx.Commit();
        return (ResultKind.Ok, ev);
    }

    // ---- Export / import ---------------------------------------------------

    public string Export()
    {
        var doc = new
        {
            format = "agedepth-export/v1",
            materials = Materials().Select(m => new { m.Id, m.Type, m.Hash, m.Json }).ToArray(),
            events = Events().Select(e => new
            {
                e.Seq, e.Id, e.Kind, e.At, e.Parents, e.ParamVersion, e.InputHash, e.Payload, e.Status,
                checksum = EventChecksum(e.Id, e.Kind, e.Payload)
            }).ToArray()
        };
        return JsonSerializer.Serialize(doc, Hashing.Json);
    }

    /// <summary>
    /// Import is atomic: any parse failure, checksum mismatch or constraint
    /// violation rolls the whole batch back, so a half-written transfer leaves
    /// the database untouched. Returns FormatCorrupt distinct from other kinds.
    /// </summary>
    public (ResultKind Kind, string Message) Import(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (ResultKind.FormatCorrupt, $"unparseable export: {ex.Message}"); }

        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "agedepth-export/v1")
            return (ResultKind.FormatCorrupt, "missing or unknown format marker");

        using var tx = _db.BeginTransaction();
        try
        {
            foreach (var m in root.GetProperty("materials").EnumerateArray())
                PutMaterial(new Material(m.GetProperty("id").GetString()!, m.GetProperty("type").GetString()!,
                    m.GetProperty("hash").GetString()!, m.GetProperty("json").GetString()!), tx);

            foreach (var e in root.GetProperty("events").EnumerateArray())
            {
                var id = e.GetProperty("id").GetString()!;
                var kind = e.GetProperty("kind").GetString()!;
                var payload = e.GetProperty("payload").GetString()!;
                var checksum = e.GetProperty("checksum").GetString()!;
                if (EventChecksum(id, kind, payload) != checksum)
                {
                    tx.Rollback();
                    return (ResultKind.FormatCorrupt, $"checksum mismatch on event {id}; batch rolled back");
                }
                AppendEvent(kind, e.GetProperty("parents").Deserialize<string[]>() ?? Array.Empty<string>(),
                    e.GetProperty("paramVersion").GetString()!, e.GetProperty("inputHash").GetString()!,
                    payload, e.GetProperty("status").GetString()!, forcedId: id,
                    forcedAt: e.GetProperty("at").GetString(), tx: tx);
            }
            tx.Commit();
            return (ResultKind.Ok, "imported");
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            tx.Rollback();
            return (ResultKind.FormatCorrupt, $"import aborted, batch rolled back: {ex.Message}");
        }
    }
}
