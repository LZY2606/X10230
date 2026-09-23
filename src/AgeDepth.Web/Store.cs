using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AgeDepth;

public sealed record EventRecord(long Seq, string Id, string Kind, string Payload, string PrevHash, string Hash, string Ts);

/// <summary>
/// SQLite 存储：materials/decisions 不可变追加，events 哈希链，derived 内容寻址，
/// models 用乐观锁支持并发冻结检测。所有写操作支持事务，半写可整体回滚。
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;

    public Store(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        Init();
    }

    public SqliteConnection Connection => _db;

    private void Init()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS materials(id TEXT PRIMARY KEY, seq INTEGER NOT NULL, kind TEXT NOT NULL, raw TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY, seq INTEGER NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, supersedes TEXT);
        CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY, id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, prev TEXT NOT NULL, hash TEXT NOT NULL, ts TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS derived(id TEXT PRIMARY KEY, kind TEXT NOT NULL, payload TEXT NOT NULL, parents TEXT NOT NULL, params TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS models(id TEXT PRIMARY KEY, name TEXT NOT NULL, candidate_id TEXT NOT NULL, frozen INTEGER NOT NULL, version INTEGER NOT NULL, payload TEXT NOT NULL, frozen_at_seq INTEGER NOT NULL DEFAULT 0);
        """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    public long NextSeq()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events";
        return (long)(cmd.ExecuteScalar() ?? 1L);
    }

    private string LastHash()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT hash FROM events ORDER BY seq DESC LIMIT 1";
        return (string?)cmd.ExecuteScalar() ?? "GENESIS";
    }

    public static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public EventRecord AppendEvent(string kind, object payload, SqliteTransaction? tx = null)
    {
        var seq = NextSeq();
        var prev = LastHash();
        var body = Json.Ser(payload);
        var id = $"evt-{seq:D6}";
        var hash = Sha(prev + "|" + id + "|" + kind + "|" + body);
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events(seq,id,kind,payload,prev,hash,ts) VALUES($s,$i,$k,$p,$pr,$h,$t)";
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$p", body);
        cmd.Parameters.AddWithValue("$pr", prev);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return new EventRecord(seq, id, kind, body, prev, hash, DateTimeOffset.UtcNow.ToString("o"));
    }

    public Outcome AddMaterial(Material m, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO materials(id,seq,kind,raw) VALUES($i,$s,$k,$r)";
        cmd.Parameters.AddWithValue("$i", m.Id);
        cmd.Parameters.AddWithValue("$s", NextSeq());
        cmd.Parameters.AddWithValue("$k", m.Kind);
        cmd.Parameters.AddWithValue("$r", Json.Ser(m));
        cmd.ExecuteNonQuery();
        return Outcome.Success;
    }

    public Outcome AddDecision(Decision d, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO decisions(id,seq,kind,payload,supersedes) VALUES($i,$s,$k,$p,$su)";
        cmd.Parameters.AddWithValue("$i", d.Id);
        cmd.Parameters.AddWithValue("$s", NextSeq());
        cmd.Parameters.AddWithValue("$k", d.Kind);
        cmd.Parameters.AddWithValue("$p", Json.Ser(d));
        cmd.Parameters.AddWithValue("$su", (object?)d.Supersedes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Outcome.Success;
    }

    public List<Material> Materials(long? atSeq = null)
    {
        var list = new List<Material>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = atSeq is null
            ? "SELECT raw FROM materials ORDER BY seq"
            : "SELECT raw FROM materials WHERE seq<=$s ORDER BY seq";
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var m = Json.De<Material>(r.GetString(0));
            if (m is not null) list.Add(m);
        }
        return list;
    }

    public List<Decision> Decisions(long? atSeq = null)
    {
        var list = new List<Decision>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = atSeq is null
            ? "SELECT payload FROM decisions ORDER BY seq"
            : "SELECT payload FROM decisions WHERE seq<=$s ORDER BY seq";
        if (atSeq is not null) cmd.Parameters.AddWithValue("$s", atSeq.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = Json.De<Decision>(r.GetString(0));
            if (d is not null) list.Add(d);
        }
        return list;
    }

    public List<EventRecord> Events(long? upTo = null)
    {
        var list = new List<EventRecord>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upTo is null ? "SELECT seq,id,kind,payload,prev,hash,ts FROM events ORDER BY seq"
                                       : "SELECT seq,id,kind,payload,prev,hash,ts FROM events WHERE seq<=$u ORDER BY seq";
        if (upTo is not null) cmd.Parameters.AddWithValue("$u", upTo.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    public void PutDerived(string kind, string id, object payload, List<string> parents, string paramsVersion, string digest, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO derived(id,kind,payload,parents,params,digest) VALUES($i,$k,$p,$pa,$pr,$d)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$p", Json.Ser(payload));
        cmd.Parameters.AddWithValue("$pa", Json.Ser(parents));
        cmd.Parameters.AddWithValue("$pr", paramsVersion);
        cmd.Parameters.AddWithValue("$d", digest);
        cmd.ExecuteNonQuery();
    }

    public string? DerivedDigest(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT digest FROM derived WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        return (string?)cmd.ExecuteScalar();
    }

    public List<(string Id, string Kind, string Payload, List<string> Parents, string Params, string Digest)> AllDerived()
    {
        var list = new List<(string, string, string, List<string>, string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,kind,payload,parents,params,digest FROM derived ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                Json.De<List<string>>(r.GetString(3)) ?? new(), r.GetString(4), r.GetString(5)));
        return list;
    }

    // ---------- 冻结模型（乐观锁） ----------
    public void CreateModel(FrozenModel m)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO models(id,name,candidate_id,frozen,version,payload,frozen_at_seq) VALUES($i,$n,$c,$f,$v,$p,$s)";
        cmd.Parameters.AddWithValue("$i", m.Id);
        cmd.Parameters.AddWithValue("$n", m.Name);
        cmd.Parameters.AddWithValue("$c", m.CandidateId);
        cmd.Parameters.AddWithValue("$f", m.Frozen ? 1 : 0);
        cmd.Parameters.AddWithValue("$v", m.Version);
        cmd.Parameters.AddWithValue("$p", Json.Ser(m));
        cmd.Parameters.AddWithValue("$s", m.FrozenAtSeq);
        cmd.ExecuteNonQuery();
    }

    public FrozenModel? GetModel(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM models WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", id);
        var p = (string?)cmd.ExecuteScalar();
        return p is null ? null : Json.De<FrozenModel>(p);
    }

    public List<FrozenModel> Models()
    {
        var list = new List<FrozenModel>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM models ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var m = Json.De<FrozenModel>(r.GetString(0)); if (m is not null) list.Add(m); }
        return list;
    }

    /// <summary>并发冻结：只有持有当前 version 的一方成功，另一方得到 Conflict。</summary>
    public Outcome Freeze(string modelId, int expectedVersion, long atSeq)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE models SET frozen=1, version=version+1, frozen_at_seq=$s WHERE id=$i AND version=$v AND frozen=0";
        cmd.Parameters.AddWithValue("$i", modelId);
        cmd.Parameters.AddWithValue("$v", expectedVersion);
        cmd.Parameters.AddWithValue("$s", atSeq);
        return cmd.ExecuteNonQuery() == 1
            ? Outcome.Success
            : Outcome.Fail(FailureKind.Conflict, $"模型 {modelId} 已被并发冻结或版本过期", $"models/{modelId}");
    }

    public SqliteTransaction BeginTx() => _db.BeginTransaction();

    public int Count(string table)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
