using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoDepth.Domain;
using Microsoft.Data.Sqlite;

namespace ChronoDepth.Storage;

public sealed record EventRecord(
    long Seq, string Id, string Type, string PayloadJson,
    string InputDigest, string ParamVersion, string[] Parents, string CreatedUtc);

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}

/// <summary>
/// Append-only event store on SQLite. Writes go through a journal row so a
/// crash mid half-write can be detected and rolled back on open. Optimistic
/// concurrency via expected sequence numbers (used by concurrent freeze).
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _conn;

    private EventStore(SqliteConnection conn) => _conn = conn;

    public static EventStore Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS journal(
                    id TEXT PRIMARY KEY, status TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events(
                    seq INTEGER PRIMARY KEY,
                    id TEXT NOT NULL UNIQUE,
                    type TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    input_digest TEXT NOT NULL,
                    param_version TEXT NOT NULL,
                    parents TEXT NOT NULL,
                    created_utc TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new EventStore(conn);
        store.Recover();
        return store;
    }

    public static EventStore OpenInMemory()
    {
        var conn = new SqliteConnection("Data Source=evtstore;Mode=Memory;Cache=Shared");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS journal(
                    id TEXT PRIMARY KEY, status TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events(
                    seq INTEGER PRIMARY KEY,
                    id TEXT NOT NULL UNIQUE,
                    type TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    input_digest TEXT NOT NULL,
                    param_version TEXT NOT NULL,
                    parents TEXT NOT NULL,
                    created_utc TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        return new EventStore(conn);
    }

    /// <summary>Roll back half-written batches: events in a pending journal are removed.</summary>
    public int Recover()
    {
        var pending = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM journal WHERE status='pending'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) pending.Add(r.GetString(0));
        }
        foreach (var id in pending)
        {
            using var tx = _conn.BeginTransaction();
            Exec("DELETE FROM events WHERE id=$id", ("$id", id), tx);
            Exec("DELETE FROM journal WHERE id=$id", ("$id", id), tx);
            tx.Commit();
        }
        return pending.Count;
    }

    public long CurrentSeq()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public EventRecord Append(
        string type, object payload, string inputDigest, string paramVersion,
        string[]? parents = null, long? expectedSeq = null, string? id = null,
        string? createdUtc = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonUtil.Options);
        id ??= "evt-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + payloadJson)))[..12];
        var stamp = createdUtc ?? DateTime.UtcNow.ToString("O");
        var parentsJson = JsonSerializer.Serialize(parents ?? Array.Empty<string>());

        using var tx = _conn.BeginTransaction();
        Exec("INSERT INTO journal(id,status,created_utc) VALUES($id,'pending',$ts)",
            ("$id", id), ("$ts", stamp), tx);

        if (expectedSeq is not null)
        {
            using var check = _conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            var actual = (long)(check.ExecuteScalar() ?? 0L);
            if (actual != expectedSeq.Value)
            {
                tx.Rollback();
                throw new ConcurrencyConflictException(
                    $"expected seq {expectedSeq.Value} but store is at {actual}");
            }
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events(id,type,payload,input_digest,param_version,parents,created_utc)
                VALUES($id,$type,$payload,$digest,$pver,$parents,$ts)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$digest", inputDigest);
            cmd.Parameters.AddWithValue("$pver", paramVersion);
            cmd.Parameters.AddWithValue("$parents", parentsJson);
            cmd.Parameters.AddWithValue("$ts", stamp);
            cmd.ExecuteNonQuery();
        }
        Exec("UPDATE journal SET status='committed' WHERE id=$id", ("$id", id), tx);
        tx.Commit();

        return All(id);
    }

    /// <summary>Import events preserving identity and order (export/import roundtrip).</summary>
    public void ImportEvents(IReadOnlyList<EventRecord> records)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var e in records)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events(seq,id,type,payload,input_digest,param_version,parents,created_utc)
                VALUES($seq,$id,$type,$payload,$digest,$pver,$parents,$ts)
                """;
            cmd.Parameters.AddWithValue("$seq", e.Seq);
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.Parameters.AddWithValue("$type", e.Type);
            cmd.Parameters.AddWithValue("$payload", e.PayloadJson);
            cmd.Parameters.AddWithValue("$digest", e.InputDigest);
            cmd.Parameters.AddWithValue("$pver", e.ParamVersion);
            cmd.Parameters.AddWithValue("$parents", JsonSerializer.Serialize(e.Parents));
            cmd.Parameters.AddWithValue("$ts", e.CreatedUtc);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<EventRecord> All(long? upToSeq = null)
    {
        var list = new List<EventRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,type,payload,input_digest,param_version,parents,created_utc FROM events ORDER BY seq"
            : "SELECT seq,id,type,payload,input_digest,param_version,parents,created_utc FROM events WHERE seq<=$upto ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$upto", upToSeq.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5),
                JsonSerializer.Deserialize<string[]>(r.GetString(6)) ?? Array.Empty<string>(),
                r.GetString(7)));
        }
        return list;
    }

    public EventRecord All(string id) => All().First(e => e.Id == id);

    /// <summary>Test hook: simulate a crash mid-write (pending journal, event row present).</summary>
    public void SimulateHalfWrite(string id)
    {
        var stamp = DateTime.UtcNow.ToString("O");
        Exec("INSERT OR REPLACE INTO journal(id,status,created_utc) VALUES($id,'pending',$ts)",
            ("$id", id), ("$ts", stamp));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(id,type,payload,input_digest,param_version,parents,created_utc)
            VALUES($id,'half-written','{}','none','none','[]',$ts)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", stamp);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, params object[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
            cmd.Parameters.AddWithValue((string)args[i], args[i + 1]);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, (string, object) p1, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(p1.Item1, p1.Item2);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql, (string, object) p1, (string, object) p2, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(p1.Item1, p1.Item2);
        cmd.Parameters.AddWithValue(p2.Item1, p2.Item2);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
