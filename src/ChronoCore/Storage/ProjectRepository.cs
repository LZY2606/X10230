using ChronoCore.Domain;
using ChronoCore.Engine;
using Microsoft.Data.Sqlite;

namespace ChronoCore.Storage;

public sealed class ProjectRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<Material> Materials { get; set; } = new();
    public List<Decision> Decisions { get; set; } = new();
    public List<ParameterSet> ParameterSets { get; set; } = new();
    public List<LedgerEvent> Events { get; set; } = new();
    public List<Branch> Branches { get; set; } = new();
    public List<DerivationResult> Results { get; set; } = new();
}

/// <summary>
/// SQLite 存储层，业务测试可直接调用。
/// 所有写操作在单事务内提交（WAL），中断自动回滚；
/// 导入前留 .preimport 备份，提交后做 integrity_check，损坏即恢复——与「格式损坏」明确区分。
/// </summary>
public sealed class ProjectRepository
{
    private readonly string _connectionString;
    public string DbPath { get; }

    public ProjectRepository(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema.Sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>integrity_check；损坏（半写）与格式/语义问题在调用方按不同结果处理。</summary>
    public bool IntegrityCheckOk(out string report)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        report = Convert.ToString(cmd.ExecuteScalar()) ?? "";
        return report == "ok";
    }

    public bool HasProjects()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects;";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void SaveProject(ProjectRecord project)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertProject(conn, tx, project);
        tx.Commit();
    }

    public ProjectRecord? LoadProject(string projectId)
    {
        using var conn = Open();
        return ReadProject(conn, projectId);
    }

    public List<string> ListProjectIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects ORDER BY id;";
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    public void DeleteProject(string projectId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "events", "decisions", "paramsets", "materials", "branches", "projects" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var col = table == "projects" ? "id" : "project_id";
            cmd.CommandText = $"DELETE FROM {table} WHERE {col}=$p;";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.ExecuteNonQuery();
        }
        using (var rc = conn.CreateCommand())
        {
            rc.Transaction = tx;
            rc.CommandText = "DELETE FROM results WHERE id NOT IN (SELECT DISTINCT result_ref FROM events WHERE result_ref IS NOT NULL);";
            rc.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AppendMaterial(string projectId, Material m)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        MaterialRows.Upsert(conn, tx, projectId, m);
        tx.Commit();
    }

    public void AppendDecision(string projectId, Decision d)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        DecisionRows.Upsert(conn, tx, projectId, d);
        tx.Commit();
    }

    public void AppendEvent(string projectId, LedgerEvent e, string? resultJson)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        EventRows.Append(conn, tx, projectId, e, resultJson);
        tx.Commit();
    }

    public string? LoadResultJson(string resultId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM results WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", resultId);
        return cmd.ExecuteScalar() as string;
    }

    public string? LoadResultJsonAtEvent(string projectId, long eventSeq)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT result_ref FROM events WHERE project_id=$p AND seq=$s;";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$s", eventSeq);
        var rid = cmd.ExecuteScalar() as string;
        return rid is null ? null : LoadResultJson(rid);
    }

    public long NextEventSeq(string projectId, string branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events WHERE project_id=$p AND branch_id=$b;";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$b", branchId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void UpsertProject(SqliteConnection conn, SqliteTransaction tx, ProjectRecord p)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO projects(id,name,created_at) VALUES($id,$n,$c) ON CONFLICT(id) DO UPDATE SET name=$n,created_at=$c;";
            cmd.Parameters.AddWithValue("$id", p.Id);
            cmd.Parameters.AddWithValue("$n", p.Name);
            cmd.Parameters.AddWithValue("$c", p.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        foreach (var b in p.Branches.DefaultIfEmpty(new Branch { Id = "main", Name = "主干", CreatedAt = p.CreatedAt }))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO branches(project_id,id,name,frozen_event_id,freeze_digest,created_at) VALUES($p,$id,$n,$f,$fd,$c) ON CONFLICT(project_id,id) DO UPDATE SET name=$n,frozen_event_id=$f,freeze_digest=$fd;";
            cmd.Parameters.AddWithValue("$p", p.Id); cmd.Parameters.AddWithValue("$id", b.Id); cmd.Parameters.AddWithValue("$n", b.Name);
            cmd.Parameters.AddWithValue("$f", (object?)b.FrozenAtEventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fd", (object?)b.FreezeDigest ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", b.CreatedAt);
            cmd.ExecuteNonQuery();
        }
        // 幂等重放：先清掉本项目旧事件行（它们引用全局 results），再整体重写；results 最后按全局引用回收
        using (var ec = conn.CreateCommand())
        {
            ec.Transaction = tx;
            ec.CommandText = "DELETE FROM events WHERE project_id=$p; DELETE FROM decisions WHERE project_id=$p; DELETE FROM materials WHERE project_id=$p; DELETE FROM paramsets WHERE project_id=$p;";
            ec.Parameters.AddWithValue("$p", p.Id);
            ec.ExecuteNonQuery();
        }
        foreach (var m in p.Materials) MaterialRows.Upsert(conn, tx, p.Id, m);
        foreach (var ps in p.ParameterSets) ParamRows.Upsert(conn, tx, p.Id, ps);
        foreach (var d in p.Decisions) DecisionRows.Upsert(conn, tx, p.Id, d);
        foreach (var r in p.Results)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO results(id,payload_json) VALUES($id,$j) ON CONFLICT(id) DO NOTHING;";
            cmd.Parameters.AddWithValue("$id", r.ResultId);
            cmd.Parameters.AddWithValue("$j", Json(r));
            cmd.ExecuteNonQuery();
        }
        foreach (var e in p.Events) EventRows.Append(conn, tx, p.Id, e, p.Results.FirstOrDefault(r => r.ResultId == e.ResultRef) is { } rr ? Json(rr) : null);
        using (var gc = conn.CreateCommand())
        {
            gc.Transaction = tx;
            gc.CommandText = "DELETE FROM results WHERE id NOT IN (SELECT DISTINCT result_ref FROM events WHERE result_ref IS NOT NULL AND result_ref <> '');";
            gc.ExecuteNonQuery();
        }
    }

    internal static string Json<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v, Web.JsonOptions.Web);

    private static ProjectRecord? ReadProject(SqliteConnection conn, string id)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, created_at FROM projects WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var p = new ProjectRecord { Id = id, Name = r.GetString(0), CreatedAt = r.GetDateTime(1) };
            r.Close();

            p.Materials = MaterialRows.ReadAll(conn, id);
            p.ParameterSets = ParamRows.ReadAll(conn, id);
            p.Decisions = DecisionRows.ReadAll(conn, id);
            p.Branches = BranchRows.ReadAll(conn, id);
            p.Events = EventRows.ReadAll(conn, id);
            using var rc = conn.CreateCommand();
            rc.CommandText = "SELECT payload_json FROM results WHERE id IN (SELECT DISTINCT result_ref FROM events WHERE project_id=$p AND result_ref IS NOT NULL);";
            rc.Parameters.AddWithValue("$p", id);
            using var rr = rc.ExecuteReader();
            while (rr.Read())
            {
                var res = System.Text.Json.JsonSerializer.Deserialize<DerivationResult>(rr.GetString(0), Web.JsonOptions.Web);
                if (res is not null) p.Results.Add(res);
            }
            return p;
        }
    }
}

