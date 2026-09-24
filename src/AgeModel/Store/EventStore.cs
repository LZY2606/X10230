using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeModel.Store;

public sealed record StoredEvent(long Seq, string Id, string Type, string Payload, string Hash, string PrevHash);

public enum FreezeStatus { Frozen, Conflict, AlreadyFrozen }

public sealed record FreezeResult(FreezeStatus Status, long Version, string? Message);

/// <summary>Append-only event store on SQLite. Events form a hash chain; every batch is
/// written inside a transaction and only then marked committed, so a crash mid-write
/// leaves uncommitted rows that recovery discards on next open.</summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public EventStore(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        Migrate();
        Recover();
    }

    private void Migrate()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              prev_hash TEXT NOT NULL,
              hash TEXT NOT NULL,
              committed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS freeze(
              model_id TEXT PRIMARY KEY,
              version INTEGER NOT NULL,
              event_seq INTEGER NOT NULL,
              payload TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Half-write recovery: drop uncommitted tail rows and verify the hash chain.</summary>
    public int Recover()
    {
        lock (_gate)
        {
            var removed = 0;
            using (var tx = _db.BeginTransaction())
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events WHERE committed = 0";
                removed = cmd.ExecuteNonQuery();
                tx.Commit();
            }
            // Verify chain integrity; a broken link truncates the log at that point.
            var events = ReadAll(includeUncommitted: false);
            string prev = "GENESIS";
            long lastGood = 0;
            foreach (var e in events)
            {
                if (e.PrevHash != prev || e.Hash != ComputeHash(e.Id, e.Type, e.Payload, e.PrevHash)) break;
                prev = e.Hash;
                lastGood = e.Seq;
            }
            if (events.Count > 0 && lastGood != events[^1].Seq)
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events WHERE seq > $s";
                cmd.Parameters.AddWithValue("$s", lastGood);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            return removed;
        }
    }

    public static string ComputeHash(string id, string type, string payload, string prevHash)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{prevHash}|{id}|{type}|{payload}")));

    public string HeadHash()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT hash FROM events WHERE committed=1 ORDER BY seq DESC LIMIT 1";
        return cmd.ExecuteScalar() as string ?? "GENESIS";
    }

    /// <summary>Append one event inside a transaction: insert uncommitted, then mark committed.</summary>
    public StoredEvent Append(string id, string type, object payload)
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(payload);
            var prev = HeadHash();
            var hash = ComputeHash(id, type, json, prev);
            using var tx = _db.BeginTransaction();
            long seq;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO events(id,type,payload,prev_hash,hash,committed) VALUES($i,$t,$p,$ph,$h,0); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$i", id);
                cmd.Parameters.AddWithValue("$t", type);
                cmd.Parameters.AddWithValue("$p", json);
                cmd.Parameters.AddWithValue("$ph", prev);
                cmd.Parameters.AddWithValue("$h", hash);
                seq = (long)(cmd.ExecuteScalar() ?? 0L);
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE events SET committed=1 WHERE seq=$s";
                cmd.Parameters.AddWithValue("$s", seq);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return new StoredEvent(seq, id, type, json, hash, prev);
        }
    }

    /// <summary>Simulates a crash mid-write: inserts an uncommitted row without finalizing.</summary>
    public void SimulateHalfWrite(string id, string type, string payload)
    {
        lock (_gate)
        {
            var prev = HeadHash();
            var hash = ComputeHash(id, type, payload, prev);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events(id,type,payload,prev_hash,hash,committed) VALUES($i,$t,$p,$ph,$h,0)";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$ph", prev);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
        }
    }

    public List<StoredEvent> ReadAll(bool includeUncommitted = false)
    {
        lock (_gate)
        {
            var list = new List<StoredEvent>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = includeUncommitted
                ? "SELECT seq,id,type,payload,hash,prev_hash FROM events ORDER BY seq"
                : "SELECT seq,id,type,payload,hash,prev_hash FROM events WHERE committed=1 ORDER BY seq";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
            return list;
        }
    }

    public long CommittedCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE committed=1";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Optimistic-concurrency freeze: exactly one of two concurrent freezes wins.</summary>
    public FreezeResult Freeze(string modelId, long expectedVersion, object payload)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            long? current;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT version FROM freeze WHERE model_id=$m";
                cmd.Parameters.AddWithValue("$m", modelId);
                var v = cmd.ExecuteScalar();
                current = v is null ? null : (long)v;
            }
            var expected = expectedVersion;
            if (current is not null && current != expected)
                return new FreezeResult(FreezeStatus.Conflict, current.Value,
                    $"model '{modelId}' is at version {current}, expected {expectedVersion}");
            var newVersion = (current ?? 0) + 1;
            var json = JsonSerializer.Serialize(payload);
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO freeze(model_id,version,event_seq,payload) VALUES($m,$v,$s,$p)
                    ON CONFLICT(model_id) DO UPDATE SET version=$v, event_seq=$s, payload=$p
                    """;
                cmd.Parameters.AddWithValue("$m", modelId);
                cmd.Parameters.AddWithValue("$v", newVersion);
                cmd.Parameters.AddWithValue("$s", CommittedCount());
                cmd.Parameters.AddWithValue("$p", json);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return new FreezeResult(FreezeStatus.Frozen, newVersion, null);
        }
    }

    public (long Version, string Payload)? GetFreeze(string modelId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT version,payload FROM freeze WHERE model_id=$m";
            cmd.Parameters.AddWithValue("$m", modelId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetInt64(0), r.GetString(1));
        }
    }

    /// <summary>Import path: inserts an event with its original identity and order.</summary>
    public void ImportRaw(long seq, string id, string type, string payload, string hash)
    {
        lock (_gate)
        {
            var prev = HeadHash();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events(seq,id,type,payload,prev_hash,hash,committed) VALUES($s,$i,$t,$p,$ph,$h,1)";
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.Parameters.AddWithValue("$ph", prev);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.ExecuteNonQuery();
        }
    }

    public void RestoreFreeze(string modelId, long version, string payload)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO freeze(model_id,version,event_seq,payload) VALUES($m,$v,$s,$p)
                ON CONFLICT(model_id) DO UPDATE SET version=$v, event_seq=$s, payload=$p
                """;
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$s", CommittedCount());
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _db.Dispose();
}