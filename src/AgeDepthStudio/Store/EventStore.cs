using System.Security.Cryptography;
using System.Text;
using AgeDepthStudio.Domain;
using Microsoft.Data.Sqlite;

namespace AgeDepthStudio.Store;

/// <summary>
/// 事件溯源存储。事件是事实来源；状态由投影重放得到。
/// 哈希链保证导出/导入后身份、顺序与来源关系不变。
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly string _path;
    private SqliteConnection _conn;

    public EventStore(string path)
    {
        _path = path;
        RecoverHalfWrittenImport();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        _conn = new SqliteConnection(cs);
        _conn.Open();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL,
              type TEXT NOT NULL,
              workspace_id TEXT NOT NULL,
              at TEXT NOT NULL,
              actor TEXT NOT NULL,
              payload TEXT NOT NULL,
              parent_hash TEXT NOT NULL,
              hash TEXT NOT NULL,
              UNIQUE(workspace_id, id));
            CREATE INDEX IF NOT EXISTS ix_events_ws ON events(workspace_id, seq);
            CREATE TABLE IF NOT EXISTS workspaces(
              workspace_id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              kind TEXT NOT NULL,
              created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>半写恢复：导入中断留下的临时文件与残留日志被清理，现有库保持可用。</summary>
    private void RecoverHalfWrittenImport()
    {
        foreach (var suffix in new[] { ".importing", ".importing-wal", ".importing-shm" })
        {
            var f = _path + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }

    public string HashOf(string type, string workspaceId, string at, string actor, string payload, string parentHash)
    {
        var canon = string.Join("\n", type, workspaceId, at, actor, payload, parentHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon))).ToLowerInvariant();
    }

    public string HeadHash(string workspaceId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM events WHERE workspace_id=$w ORDER BY seq DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        return (string?)cmd.ExecuteScalar() ?? "genesis";
    }

    public StudioEvent Append(string type, string workspaceId, string actor, string payload, string? id = null, string? at = null)
    {
        var parent = HeadHash(workspaceId);
        var evtId = id ?? $"evt_{Guid.NewGuid():N}";
        var atv = at ?? DateTimeOffset.UtcNow.ToString("o");
        var hash = HashOf(type, workspaceId, atv, actor, payload, parent);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(id,type,workspace_id,at,actor,payload,parent_hash,hash)
            VALUES($id,$t,$w,$at,$a,$p,$ph,$h);
            SELECT seq FROM events WHERE rowid=last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", evtId);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$w", workspaceId);
        cmd.Parameters.AddWithValue("$at", atv);
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$ph", parent);
        cmd.Parameters.AddWithValue("$h", hash);
        var seq = (long)cmd.ExecuteScalar()!;
        return new StudioEvent(seq, evtId, type, workspaceId, atv, actor, payload, parent, hash);
    }

    public void AppendMany(IEnumerable<(string type, string actor, string payload)> batch, string workspaceId)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var (type, actor, payload) in batch) Append(type, workspaceId, actor, payload);
        tx.Commit();
    }

    public IReadOnlyList<StudioEvent> Events(string workspaceId, long? upToSeq = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,type,workspace_id,at,actor,payload,parent_hash,hash FROM events WHERE workspace_id=$w ORDER BY seq"
            : "SELECT seq,id,type,workspace_id,at,actor,payload,parent_hash,hash FROM events WHERE workspace_id=$w AND seq<=$s ORDER BY seq";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$s", upToSeq.Value);
        var list = new List<StudioEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StudioEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));
        return list;
    }

    public bool VerifyChain(string workspaceId)
    {
        var parent = "genesis";
        foreach (var e in Events(workspaceId))
        {
            if (e.ParentHash != parent) return false;
            if (HashOf(e.Type, e.WorkspaceId, e.At, e.Actor, e.PayloadJson, e.ParentHash) != e.Hash) return false;
            parent = e.Hash;
        }
        return true;
    }

    public void CreateWorkspace(string workspaceId, string name, string kind)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO workspaces(workspace_id,name,kind,created_at) VALUES($i,$n,$k,$t)";
        cmd.Parameters.AddWithValue("$i", workspaceId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Id, string Name, string Kind)> Workspaces()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT workspace_id,name,kind FROM workspaces ORDER BY workspace_id";
        var list = new List<(string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public bool WorkspaceExists(string workspaceId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM workspaces WHERE workspace_id=$w";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// 并发冻结：在 IMMEDIATE 事务里校验 token（当前 head 参数版本），
    /// 两个冻结请求竞争时恰有一个成功，另一个得到带审计事件的冲突。
    /// </summary>
    public bool TryFreeze(string workspaceId, string modelId, string expectedParamsId, string actor, out StudioEvent? conflictEvent)
    {
        conflictEvent = null;
        using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var state = Projector.Project(Events(workspaceId));
        var head = state.HeadParamsId;
        if (head != expectedParamsId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                rule = "optimistic-freeze-token",
                modelId,
                expectedParamsId,
                actualParamsId = head,
                basis = "并发冻结检验：后到的冻结请求被拒绝并留下审计事件，先到者生效"
            });
            conflictEvent = Append("freeze.conflict", workspaceId, actor, payload);
            tx.Commit();
            return false;
        }
        if (state.Freeze is not null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                rule = "already-frozen",
                modelId,
                heldBy = state.Freeze.Actor,
                basis = "并发冻结检验：模型已被冻结，重复冻结被拒绝"
            });
            conflictEvent = Append("freeze.conflict", workspaceId, actor, payload);
            tx.Commit();
            return false;
        }
        Append("model.frozen", workspaceId, actor, JsonSerializer.Serialize(new
        {
            modelId,
            paramsId = expectedParamsId,
            token = Guid.NewGuid().ToString("N"),
            basis = "冻结后模型不可变；新锚点只能以非破坏试验比对影响"
        }));
        tx.Commit();
        return true;
    }

    // ---------- 导出 / 导入 ----------

    public string ExportArchive(string workspaceId)
    {
        var ws = Workspaces().First(w => w.Id == workspaceId);
        var doc = new
        {
            format = "agedepth-archive/1",
            workspace = new { id = ws.Id, name = ws.Name, kind = ws.Kind },
            events = Events(workspaceId).Select(e => new
            {
                e.Seq, e.Id, e.Type, e.At, e.Actor,
                payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson),
                e.ParentHash, e.Hash
            }).ToArray()
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 导入归档：先写入临时库并校验哈希链，成功后原子替换；
    /// 中途崩溃时现有库不受影响（半写恢复）。
    /// </summary>
    public void ImportArchive(string archiveJson)
    {
        using var doc = JsonDocument.Parse(archiveJson); // 格式损坏在此抛出，区别于语义失败
        var root = doc.RootElement;
        if (root.GetProperty("format").GetString() != "agedepth-archive/1")
            throw new InvalidDataException("unsupported archive format");
        var wsId = root.GetProperty("workspace").GetProperty("id").GetString()!;
        var wsName = root.GetProperty("workspace").GetProperty("name").GetString()!;
        var wsKind = root.GetProperty("workspace").GetProperty("kind").GetString()!;
        var events = root.GetProperty("events").EnumerateArray()
            .Select(e => new StudioEvent(
                e.GetProperty("Seq").GetInt64(),
                e.GetProperty("Id").GetString()!,
                e.GetProperty("Type").GetString()!,
                wsId,
                e.GetProperty("At").GetString()!,
                e.GetProperty("Actor").GetString()!,
                e.GetProperty("payload").GetRawText(),
                e.GetProperty("ParentHash").GetString()!,
                e.GetProperty("Hash").GetString()!))
            .OrderBy(e => e.Seq).ToList();

        // 先校验链：身份、顺序、来源关系必须在往返后保持。
        var parent = "genesis";
        foreach (var e in events)
        {
            if (e.ParentHash != parent || HashOf(e.Type, wsId, e.At, e.Actor, e.PayloadJson, e.ParentHash) != e.Hash)
                throw new InvalidDataException($"archive hash chain broken at {e.Id}");
            parent = e.Hash;
        }

        var tmp = _path + ".importing";
        foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm" }) if (File.Exists(f)) File.Delete(f);
        using (var import = new EventStore(tmp))
        {
            import.CreateWorkspace(wsId, wsName, wsKind);
            foreach (var e in events)
                import.Append(e.Type, wsId, e.Actor, e.PayloadJson, e.Id, e.At);
        }
        // 原子替换：先备份现有库，再换入；任何一步失败都保留可恢复副本。
        var backup = _path + ".backup";
        _conn.Close();
        if (File.Exists(_path)) File.Copy(_path, backup, overwrite: true);
        File.Copy(tmp, _path, overwrite: true);
        foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm" }) if (File.Exists(f)) File.Delete(f);
        // 重新打开主连接。
        var cs = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        var fresh = new SqliteConnection(cs);
        fresh.Open();
        using (var cmd = fresh.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        SetConnection(fresh);
        if (File.Exists(backup)) File.Delete(backup);
    }

    private void SetConnection(SqliteConnection conn)
    {
        _conn.Dispose();
        _conn = conn;
    }


    /// <summary>删除本地数据库并立即重建（供审阅页做“删除再导入”往返验证）。</summary>
    public void Reset()
    {
        _conn.Close();
        _conn.Dispose();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        var cs = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        var fresh = new SqliteConnection(cs);
        fresh.Open();
        using (var cmd = fresh.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }
        _conn = fresh;
        Migrate();
    }

    public void Dispose() => _conn.Dispose();
}
