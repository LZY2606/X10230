using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DepthAge.Core;

public enum FreezeOutcome { Frozen, AlreadyFrozen }

/// <summary>导入结果：格式损坏必须与语义无解/预算耗尽可区分。</summary>
public abstract record ImportResult
{
    public sealed record Ok(int Materials, int Events, int Freezes) : ImportResult;
    public sealed record CorruptFormat(string Reason, string Position) : ImportResult;
}

/// <summary>
/// SQLite 事件存储。
///  - 材料不可变（内容哈希身份），事件只能追加；
///  - 两阶段写入：BeginWrite 登记 pending，CompleteWrite 核销；启动时 Recover() 回滚所有
///    未核销写入（半写恢复），已提交写入不受影响；
///  - 冻结用主键冲突实现并发互斥：并发冻结同一模型只有一个成功。
/// </summary>
public sealed class DepthAgeStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _sync = new();

    public string Path { get; }

    public DepthAgeStore(string path)
    {
        Path = path;
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS materials(
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, hash TEXT NOT NULL,
                    raw_json TEXT NOT NULL, seq INTEGER NOT NULL, created TEXT NOT NULL,
                    write_token TEXT);
                CREATE TABLE IF NOT EXISTS events(
                    seq INTEGER PRIMARY KEY, id TEXT NOT NULL, kind TEXT NOT NULL,
                    payload TEXT NOT NULL, input_digest TEXT NOT NULL, param_version TEXT NOT NULL,
                    parents TEXT NOT NULL, created TEXT NOT NULL, write_token TEXT);
                CREATE TABLE IF NOT EXISTS pending_writes(token TEXT PRIMARY KEY, created TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS freezes(
                    model_id TEXT NOT NULL, branch TEXT NOT NULL, by TEXT NOT NULL,
                    seq INTEGER NOT NULL, created TEXT NOT NULL,
                    PRIMARY KEY(model_id, branch));
                """;
            cmd.ExecuteNonQuery();
        }
        Recover();
    }

    /// <summary>半写恢复：删除所有仍挂在 pending token 下的材料与事件，然后清空 pending。返回回滚行数。</summary>
    public int Recover()
    {
        lock (_sync)
        {
            var removed = 0;
            removed += Exec("DELETE FROM materials WHERE write_token IN (SELECT token FROM pending_writes)");
            removed += Exec("DELETE FROM events WHERE write_token IN (SELECT token FROM pending_writes)");
            Exec("DELETE FROM pending_writes");
            return removed;
        }
    }

    private int Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    public string BeginWrite()
    {
        lock (_sync)
        {
            var token = "w_" + Guid.NewGuid().ToString("N");
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO pending_writes(token, created) VALUES ($t, $c)";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            return token;
        }
    }

    public void CompleteWrite(string token)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_writes WHERE token=$t";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.ExecuteNonQuery();
        }
    }

    public MaterialEnvelope AddMaterial(string kind, string rawJson, long seq, string? writeToken = null)
    {
        lock (_sync)
        {
            var env = MaterialHashes.Create(kind, rawJson, seq);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO materials(id, kind, hash, raw_json, seq, created, write_token)
                VALUES ($id, $kind, $hash, $raw, $seq, $created, $token)
                """;
            cmd.Parameters.AddWithValue("$id", env.Id);
            cmd.Parameters.AddWithValue("$kind", env.Kind);
            cmd.Parameters.AddWithValue("$hash", env.Hash);
            cmd.Parameters.AddWithValue("$raw", env.RawJson);
            cmd.Parameters.AddWithValue("$seq", env.Seq);
            cmd.Parameters.AddWithValue("$created", env.Created);
            cmd.Parameters.AddWithValue("$token", (object?)writeToken ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return env;
        }
    }

    public ModelEvent AppendEvent(string kind, string payloadJson, string inputDigest,
        string paramVersion, IReadOnlyList<string> parents, string? writeToken = null)
    {
        lock (_sync)
        {
            long seq;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events";
                seq = (long)(cmd.ExecuteScalar() ?? 1L);
            }
            var id = $"evt-{seq:D6}";
            var created = DateTimeOffset.UtcNow.ToString("O");
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO events(seq, id, kind, payload, input_digest, param_version, parents, created, write_token)
                    VALUES ($seq, $id, $kind, $payload, $digest, $pv, $parents, $created, $token)
                    """;
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$payload", payloadJson);
                cmd.Parameters.AddWithValue("$digest", inputDigest);
                cmd.Parameters.AddWithValue("$pv", paramVersion);
                cmd.Parameters.AddWithValue("$parents", JsonOptions.Serialize(parents));
                cmd.Parameters.AddWithValue("$created", created);
                cmd.Parameters.AddWithValue("$token", (object?)writeToken ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            return new ModelEvent(seq, id, kind, payloadJson, inputDigest, paramVersion, parents, created);
        }
    }

    public IReadOnlyList<MaterialEnvelope> Materials(long? upToSeq = null)
    {
        lock (_sync)
        {
            var limit = upToSeq ?? long.MaxValue;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, hash, raw_json, seq, created FROM materials WHERE seq <= $lim ORDER BY id";
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<MaterialEnvelope>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new MaterialEnvelope(r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetString(3), r.GetInt64(4), r.GetString(5)));
            return list;
        }
    }

    public IReadOnlyList<ModelEvent> Events(long? upToSeq = null)
    {
        lock (_sync)
        {
            var limit = upToSeq ?? long.MaxValue;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, id, kind, payload, input_digest, param_version, parents, created FROM events WHERE seq <= $lim ORDER BY seq";
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<ModelEvent>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ModelEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5),
                    JsonOptions.Deserialize<List<string>>(r.GetString(6)) ?? new List<string>(),
                    r.GetString(7)));
            return list;
        }
    }

    public long MaxSeq()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>并发冻结：PRIMARY KEY(model_id, branch) 冲突即失败，先到先得。</summary>
    public FreezeOutcome TryFreeze(string modelId, string branch, string by, long seq)
    {
        lock (_sync)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO freezes(model_id, branch, by, seq, created) VALUES ($m, $b, $by, $s, $c)";
                cmd.Parameters.AddWithValue("$m", modelId);
                cmd.Parameters.AddWithValue("$b", branch);
                cmd.Parameters.AddWithValue("$by", by);
                cmd.Parameters.AddWithValue("$s", seq);
                cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
                return FreezeOutcome.Frozen;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return FreezeOutcome.AlreadyFrozen;
            }
        }
    }

    public IReadOnlyList<FreezeRow> Freezes(long? upToSeq = null)
    {
        lock (_sync)
        {
            var limit = upToSeq ?? long.MaxValue;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT model_id, branch, by, seq, created FROM freezes WHERE seq <= $lim ORDER BY seq";
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<FreezeRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new FreezeRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)));
            return list;
        }
    }

    // ---------- 导出 / 导入（身份、顺序、来源关系全部保留） ----------

    public string ExportJson()
    {
        lock (_sync)
        {
            var doc = new
            {
                format = "depthage-export/1",
                exported = DateTimeOffset.UtcNow.ToString("O"),
                materials = Materials().Select(m => new
                {
                    id = m.Id, kind = m.Kind, hash = m.Hash,
                    rawJson = m.RawJson,
                    seq = m.Seq, created = m.Created
                }).ToArray(),
                events = Events().Select(e => new
                {
                    seq = e.Seq, id = e.Id, kind = e.Kind,
                    payload = JsonDocument.Parse(e.PayloadJson).RootElement,
                    inputDigest = e.InputDigest, paramVersion = e.ParamVersion,
                    parents = e.Parents, created = e.Created
                }).ToArray(),
                freezes = Freezes().Select(f => new
                {
                    modelId = f.ModelId, branch = f.Branch, by = f.By, seq = f.Seq, created = f.Created
                }).ToArray()
            };
            return JsonOptions.Serialize(doc, pretty: true);
        }
    }

    public ImportResult ImportJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return new ImportResult.CorruptFormat($"unparseable JSON: {ex.Message}", "$"); }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new ImportResult.CorruptFormat("export root must be an object", "$");
        if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "depthage-export/1")
            return new ImportResult.CorruptFormat("missing or unknown format tag (expected depthage-export/1)", "$.format");

        lock (_sync)
        {
            var materials = root.GetProperty("materials");
            var events = root.GetProperty("events");
            var freezes = root.GetProperty("freezes");
            var mCount = 0;
            var i = 0;
            foreach (var m in materials.EnumerateArray())
            {
                var pos = $"$.materials[{i}]";
                var raw = m.GetProperty("rawJson").GetString() ?? "";
                var hash = m.GetProperty("hash").GetString() ?? "";
                if (MaterialHashes.Sha256Hex(raw) != hash)
                    return new ImportResult.CorruptFormat($"material hash mismatch for {m.GetProperty("id").GetString()}", pos);
                var id = m.GetProperty("id").GetString() ?? "";
                if (MaterialHashes.Create(m.GetProperty("kind").GetString() ?? "", raw, 0).Id != id)
                    return new ImportResult.CorruptFormat($"material id does not match content hash: {id}", pos);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO materials(id, kind, hash, raw_json, seq, created, write_token)
                    VALUES ($id, $kind, $hash, $raw, $seq, $created, NULL)
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$kind", m.GetProperty("kind").GetString() ?? "");
                cmd.Parameters.AddWithValue("$hash", hash);
                cmd.Parameters.AddWithValue("$raw", raw);
                cmd.Parameters.AddWithValue("$seq", m.GetProperty("seq").GetInt64());
                cmd.Parameters.AddWithValue("$created", m.GetProperty("created").GetString() ?? "");
                mCount += cmd.ExecuteNonQuery();
                i++;
            }
            var eCount = 0;
            i = 0;
            foreach (var e in events.EnumerateArray())
            {
                var pos = $"$.events[{i}]";
                var seq = e.GetProperty("seq").GetInt64();
                var id = e.GetProperty("id").GetString() ?? "";
                if (id != $"evt-{seq:D6}")
                    return new ImportResult.CorruptFormat($"event id {id} does not match seq {seq}", pos);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO events(seq, id, kind, payload, input_digest, param_version, parents, created, write_token)
                    VALUES ($seq, $id, $kind, $payload, $digest, $pv, $parents, $created, NULL)
                    """;
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$kind", e.GetProperty("kind").GetString() ?? "");
                cmd.Parameters.AddWithValue("$payload", e.GetProperty("payload").GetRawText());
                cmd.Parameters.AddWithValue("$digest", e.GetProperty("inputDigest").GetString() ?? "");
                cmd.Parameters.AddWithValue("$pv", e.GetProperty("paramVersion").GetString() ?? "");
                cmd.Parameters.AddWithValue("$parents", e.GetProperty("parents").GetRawText());
                cmd.Parameters.AddWithValue("$created", e.GetProperty("created").GetString() ?? "");
                eCount += cmd.ExecuteNonQuery();
                i++;
            }
            var fCount = 0;
            foreach (var f in freezes.EnumerateArray())
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO freezes(model_id, branch, by, seq, created) VALUES ($m, $b, $by, $s, $c)";
                cmd.Parameters.AddWithValue("$m", f.GetProperty("modelId").GetString() ?? "");
                cmd.Parameters.AddWithValue("$b", f.GetProperty("branch").GetString() ?? "");
                cmd.Parameters.AddWithValue("$by", f.GetProperty("by").GetString() ?? "");
                cmd.Parameters.AddWithValue("$s", f.GetProperty("seq").GetInt64());
                cmd.Parameters.AddWithValue("$c", f.GetProperty("created").GetString() ?? "");
                fCount += cmd.ExecuteNonQuery();
            }
            return new ImportResult.Ok(mCount, eCount, fCount);
        }
    }

    public void Dispose() => _conn.Dispose();
}
