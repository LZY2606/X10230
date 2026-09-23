using System.Text.Json;
using AgeDepthLab.Core;
using Microsoft.Data.Sqlite;

namespace AgeDepthLab.Storage;

public sealed record StoredEvent(
    long Seq, string Id, string Type, string Payload,
    string InputHash, int ParamVersion, string Parents, string CreatedAt);

public sealed record StoredMaterial(string Id, string Kind, string Payload, string Hash, long EventSeq);

public sealed record FreezeRecord(string Id, string CandidateId, long ModelVersion, long EventSeq, string Note);

/// <summary>
/// SQLite 事件库。事件只追加；材料为不可变投影；所有多步写入都在事务内，
/// 半写（进程中断/截断输入）不会留下部分状态。
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public EventStore(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              input_hash TEXT NOT NULL DEFAULT '',
              param_version INTEGER NOT NULL DEFAULT 0,
              parents TEXT NOT NULL DEFAULT '[]',
              created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS materials(
              id TEXT NOT NULL,
              kind TEXT NOT NULL,
              payload TEXT NOT NULL,
              hash TEXT NOT NULL,
              event_seq INTEGER NOT NULL,
              ord INTEGER NOT NULL,
              PRIMARY KEY(id, kind));
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS freezes(
              id TEXT PRIMARY KEY,
              candidate_id TEXT NOT NULL,
              model_version INTEGER NOT NULL,
              event_seq INTEGER NOT NULL,
              note TEXT NOT NULL DEFAULT '');
            INSERT OR IGNORE INTO meta(key,value) VALUES('model_version','0');
            INSERT OR IGNORE INTO meta(key,value) VALUES('param_version','1');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();

    public long NextSeq()
    {
        lock (_gate)
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public StoredEvent Append(string type, object payload, string inputHash, int paramVersion, IReadOnlyList<string> parents)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var ev = AppendInTx(tx, type, payload, inputHash, paramVersion, parents);
            tx.Commit();
            return ev;
        }
    }

    private StoredEvent AppendInTx(SqliteTransaction tx, string type, object payload, string inputHash, int paramVersion, IReadOnlyList<string> parents)
    {
        var id = "ev-" + Guid.NewGuid().ToString("N")[..12];
        var payloadJson = payload is string s ? s : JsonSerializer.Serialize(payload, JsonOptions.Default);
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events(id,type,payload,input_hash,param_version,parents,created_at)
            VALUES($id,$type,$payload,$ih,$pv,$parents,$ts);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.Parameters.AddWithValue("$ih", inputHash);
        cmd.Parameters.AddWithValue("$pv", paramVersion);
        cmd.Parameters.AddWithValue("$parents", JsonSerializer.Serialize(parents));
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        var seq = (long)cmd.ExecuteScalar()!;
        return new StoredEvent(seq, id, type, payloadJson, inputHash, paramVersion,
            JsonSerializer.Serialize(parents), DateTimeOffset.UtcNow.ToString("O"));
    }

    public void PutMaterial(SqliteTransaction tx, string id, string kind, string payload, string hash, long eventSeq, int ord)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO materials(id,kind,payload,hash,event_seq,ord) VALUES($id,$kind,$payload,$hash,$seq,$ord)
            ON CONFLICT(id,kind) DO UPDATE SET payload=excluded.payload, hash=excluded.hash,
              event_seq=excluded.event_seq, ord=excluded.ord;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$seq", eventSeq);
        cmd.Parameters.AddWithValue("$ord", ord);
        cmd.ExecuteNonQuery();
    }

    /// <summary>材料导入：事件与投影在同一事务内提交，半写即整体回滚。</summary>
    public StoredEvent ImportMaterials(MaterialSet ms)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var ev = AppendInTx(tx, "materials.imported", ms, ms.ContentHash(), GetParamVersionInTx(), Array.Empty<string>());
            int ord = 0;
            void Put(string id, string kind, object payload)
                => PutMaterial(tx, id, kind, JsonSerializer.Serialize(payload, JsonOptions.Default),
                       Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                           System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions.Default))))[..16],
                       ev.Seq, ord++);
            Put(ms.Datum.Id, "datum", ms.Datum);
            foreach (var s in ms.Segments) Put(s.Id, "segment", s);
            foreach (var b in ms.Boundaries) Put(b.Id, "boundary", b);
            foreach (var a in ms.Anchors) Put(a.Id, "anchor", a);
            foreach (var e in ms.EventLayers) Put(e.Id, "eventlayer", e);
            tx.Commit();
            return ev;
        }
    }

    public MaterialSet? CurrentMaterials()
    {
        lock (_gate)
        {
            var rows = new List<(string kind, string payload, int ord)>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT kind,payload,ord FROM materials ORDER BY ord";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
            }
            if (rows.Count == 0) return null;
            DepthDatum? datum = null;
            var segs = new List<CoreSegment>(); var bnds = new List<SegmentBoundary>();
            var anchors = new List<AgeAnchor>(); var layers = new List<EventLayer>();
            foreach (var (kind, payload, _) in rows)
            {
                switch (kind)
                {
                    case "datum": datum = JsonSerializer.Deserialize<DepthDatum>(payload, JsonOptions.Default); break;
                    case "segment": segs.Add(JsonSerializer.Deserialize<CoreSegment>(payload, JsonOptions.Default)!); break;
                    case "boundary": bnds.Add(JsonSerializer.Deserialize<SegmentBoundary>(payload, JsonOptions.Default)!); break;
                    case "anchor": anchors.Add(JsonSerializer.Deserialize<AgeAnchor>(payload, JsonOptions.Default)!); break;
                    case "eventlayer": layers.Add(JsonSerializer.Deserialize<EventLayer>(payload, JsonOptions.Default)!); break;
                }
            }
            return datum == null ? null : new MaterialSet(datum, segs, bnds, anchors, layers);
        }
    }

    public IReadOnlyList<StoredEvent> Events(long? asOfSeq = null)
    {
        lock (_gate)
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = asOfSeq == null
                ? "SELECT seq,id,type,payload,input_hash,param_version,parents,created_at FROM events ORDER BY seq"
                : "SELECT seq,id,type,payload,input_hash,param_version,parents,created_at FROM events WHERE seq<=$s ORDER BY seq";
            if (asOfSeq != null) cmd.Parameters.AddWithValue("$s", asOfSeq.Value);
            var list = new List<StoredEvent>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetString(7)));
            return list;
        }
    }

    public int GetParamVersion()
    {
        lock (_gate) return GetParamVersionInTx();
    }

    private int GetParamVersionInTx()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='param_version'";
        return int.Parse((string)cmd.ExecuteScalar()!);
    }

    public int BumpParamVersion()
    {
        lock (_gate)
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE meta SET value=CAST(CAST(value AS INTEGER)+1 AS TEXT) WHERE key='param_version'; SELECT value FROM meta WHERE key='param_version'";
            return int.Parse((string)cmd.ExecuteScalar()!);
        }
    }

    public long ModelVersion()
    {
        lock (_gate)
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key='model_version'";
            return long.Parse((string)cmd.ExecuteScalar()!);
        }
    }

    /// <summary>并发冻结：期望版本不匹配即冲突，由事务保证只有一个冻结成功。</summary>
    public (bool ok, FreezeRecord? rec, long currentVersion) TryFreeze(
        string candidateId, long expectedVersion, string note, string inputHash, int paramVersion)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            long current;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT value FROM meta WHERE key='model_version'";
                current = long.Parse((string)cmd.ExecuteScalar()!);
            }
            if (current != expectedVersion)
            {
                tx.Rollback();
                return (false, null, current);
            }
            var ev = AppendInTx(tx, "model.frozen",
                new { candidateId, expectedVersion, note }, inputHash, paramVersion, Array.Empty<string>());
            var id = "frz-" + Guid.NewGuid().ToString("N")[..10];
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO freezes(id,candidate_id,model_version,event_seq,note) VALUES($id,$cid,$mv,$seq,$note);
                    UPDATE meta SET value=CAST(CAST(value AS INTEGER)+1 AS TEXT) WHERE key='model_version';
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$cid", candidateId);
                cmd.Parameters.AddWithValue("$mv", current + 1);
                cmd.Parameters.AddWithValue("$seq", ev.Seq);
                cmd.Parameters.AddWithValue("$note", note);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return (true, new FreezeRecord(id, candidateId, current + 1, ev.Seq, note), current + 1);
        }
    }

    public IReadOnlyList<FreezeRecord> Freezes()
    {
        lock (_gate)
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,candidate_id,model_version,event_seq,note FROM freezes ORDER BY model_version";
            var list = new List<FreezeRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new FreezeRecord(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4)));
            return list;
        }
    }
}
