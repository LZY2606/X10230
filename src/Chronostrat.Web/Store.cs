using System.Text.Json;
using System.Text.Json.Nodes;
using Chronostrat.Core;
using Microsoft.Data.Sqlite;

namespace Chronostrat.Web;

/// <summary>
/// SQLite-backed append-only event log. Appends are transactional; archive
/// import is all-or-nothing so a crash mid-import cannot half-write.
/// </summary>
public sealed class SqliteEventLog : IEventLog, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SqliteEventLog(string path)
    {
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                type TEXT NOT NULL,
                payload TEXT NOT NULL,
                depends_on TEXT NOT NULL,
                input_digest TEXT,
                param_version INTEGER NOT NULL,
                recorded_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public long CurrentSeq
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
                return (long)cmd.ExecuteScalar()!;
            }
        }
    }

    public IReadOnlyList<Event> Read(long? upToSeq = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = upToSeq is null
                ? "SELECT id,seq,type,payload,depends_on,input_digest,param_version,recorded_at FROM events ORDER BY seq"
                : "SELECT id,seq,type,payload,depends_on,input_digest,param_version,recorded_at FROM events WHERE seq<=$s ORDER BY seq";
            if (upToSeq is { } s) cmd.Parameters.AddWithValue("$s", s);
            var list = new List<Event>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Event(
                    r.GetString(0), r.GetInt64(1), r.GetString(2),
                    JsonNode.Parse(r.GetString(3))!.AsObject(),
                    JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? Array.Empty<string>(),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetInt32(6), r.GetString(7)));
            }
            return list;
        }
    }

    public Event Append(string type, JsonObject payload, string[] dependsOn, string? inputDigest, int paramVersion)
    {
        lock (_gate) return AppendLocked(type, payload, dependsOn, inputDigest, paramVersion, null)!;
    }

    public Event? AppendIfUnchanged(string type, JsonObject payload, string[] dependsOn, string? inputDigest, int paramVersion, long expectedSeq)
    {
        lock (_gate) return AppendLocked(type, payload, dependsOn, inputDigest, paramVersion, expectedSeq);
    }

    private Event? AppendLocked(string type, JsonObject payload, string[] dependsOn, string? inputDigest, int paramVersion, long? expectedSeq)
    {
        using var tx = _conn.BeginTransaction();
        if (expectedSeq is { } expected)
        {
            using var check = _conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            long current = (long)check.ExecuteScalar()!;
            if (current != expected)
            {
                tx.Rollback();
                return null;
            }
        }
        string at = DateTimeOffset.UtcNow.ToString("o");
        string id = Event.ComputeId(type, payload, at);
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events(id,type,payload,depends_on,input_digest,param_version,recorded_at)
            VALUES($id,$type,$payload,$deps,$digest,$pv,$at)
            RETURNING seq;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$payload", CanonicalJson.Serialize(payload));
        cmd.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(dependsOn));
        cmd.Parameters.AddWithValue("$digest", (object?)inputDigest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pv", paramVersion);
        cmd.Parameters.AddWithValue("$at", at);
        long seq = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return new Event(id, seq, type, (JsonObject)payload.DeepClone(), dependsOn, inputDigest, paramVersion, at);
    }

    /// <summary>All-or-nothing archive import; existing ids are skipped (idempotent).</summary>
    public ArchiveOutcome ImportArchive(string text)
    {
        var validation = Archive.Validate(text, out var events);
        if (validation is not ArchiveOutcome.Ok) return validation;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var e in events)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR IGNORE INTO events(seq,id,type,payload,depends_on,input_digest,param_version,recorded_at)
                    VALUES($seq,$id,$type,$payload,$deps,$digest,$pv,$at);
                    """;
                cmd.Parameters.AddWithValue("$seq", e.Seq);
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.Parameters.AddWithValue("$type", e.Type);
                cmd.Parameters.AddWithValue("$payload", CanonicalJson.Serialize(e.Payload));
                cmd.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(e.DependsOn));
                cmd.Parameters.AddWithValue("$digest", (object?)e.InputDigest ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pv", e.ParamVersion);
                cmd.Parameters.AddWithValue("$at", e.RecordedAt);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return validation;
    }

    public string ExportArchive() => Archive.Export(Read());

    /// <summary>Atomic file write: temp file + rename, so readers never see a half-written export.</summary>
    public void ExportArchiveTo(string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ExportArchive());
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose() => _conn.Dispose();
}
