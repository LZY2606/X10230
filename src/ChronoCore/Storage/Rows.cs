using ChronoCore.Domain;
using ChronoCore.Engine;
using Microsoft.Data.Sqlite;

namespace ChronoCore.Storage;

internal static class RowUtil
{
    public static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string, object?)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, val) in ps)
        {
            var p = cmd.Parameters.AddWithValue(name, val ?? DBNull.Value);
            if (val is DateTime dt) p.SqliteType = SqliteType.Text;
        }
        cmd.ExecuteNonQuery();
    }
}

internal static class MaterialRows
{
    public static void Upsert(SqliteConnection conn, SqliteTransaction? tx, string projectId, Material m)
        => RowUtil.Exec(conn, tx, """
INSERT INTO materials(project_id,id,kind,payload_json) VALUES($p,$id,$k,$j)
ON CONFLICT(project_id,id) DO UPDATE SET kind=$k,payload_json=$j;
""", ("$p", projectId), ("$id", m.Id), ("$k", (int)m.Kind), ("$j", ProjectRepository.Json(m)));

    public static List<Material> ReadAll(SqliteConnection conn, string projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM materials WHERE project_id=$p ORDER BY id;";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<Material>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(System.Text.Json.JsonSerializer.Deserialize<Material>(r.GetString(0), Web.JsonOptions.Web)!);
        return list;
    }
}

internal static class ParamRows
{
    public static void Upsert(SqliteConnection conn, SqliteTransaction? tx, string projectId, ParameterSet ps)
        => RowUtil.Exec(conn, tx, """
INSERT INTO paramsets(project_id,id,payload_json) VALUES($p,$id,$j)
ON CONFLICT(project_id,id) DO UPDATE SET payload_json=$j;
""", ("$p", projectId), ("$id", ps.Id), ("$j", ProjectRepository.Json(ps)));

    public static List<ParameterSet> ReadAll(SqliteConnection conn, string projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM paramsets WHERE project_id=$p ORDER BY id;";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<ParameterSet>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(System.Text.Json.JsonSerializer.Deserialize<ParameterSet>(r.GetString(0), Web.JsonOptions.Web)!);
        return list;
    }
}

internal static class DecisionRows
{
    public static void Upsert(SqliteConnection conn, SqliteTransaction? tx, string projectId, Decision d)
        => RowUtil.Exec(conn, tx, """
INSERT INTO decisions(project_id,id,payload_json) VALUES($p,$id,$j)
ON CONFLICT(project_id,id) DO UPDATE SET payload_json=$j;
""", ("$p", projectId), ("$id", d.Id), ("$j", ProjectRepository.Json(d)));

    public static List<Decision> ReadAll(SqliteConnection conn, string projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM decisions WHERE project_id=$p ORDER BY id;";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<Decision>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(System.Text.Json.JsonSerializer.Deserialize<Decision>(r.GetString(0), Web.JsonOptions.Web)!);
        return list;
    }
}

internal static class BranchRows
{
    public static List<Branch> ReadAll(SqliteConnection conn, string projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,frozen_event_id,freeze_digest,created_at FROM branches WHERE project_id=$p ORDER BY id;";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<Branch>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Branch
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                FrozenAtEventId = r.IsDBNull(2) ? null : r.GetString(2),
                FreezeDigest = r.IsDBNull(3) ? null : r.GetString(3),
                CreatedAt = r.GetDateTime(4)
            });
        return list;
    }
}

internal static class EventRows
{
    public static void Append(SqliteConnection conn, SqliteTransaction? tx, string projectId, LedgerEvent e, string? resultJson)
    {
        if (resultJson is not null && !string.IsNullOrEmpty(e.ResultRef))
            RowUtil.Exec(conn, tx, "INSERT INTO results(id,payload_json) VALUES($id,$j) ON CONFLICT(id) DO NOTHING;",
                ("$id", e.ResultRef!), ("$j", resultJson));
        RowUtil.Exec(conn, tx, """
INSERT INTO events(project_id,branch_id,seq,id,parent_event_id,type,parameter_set_id,input_digest,payload_json,result_ref,actor,created_at)
VALUES($p,$b,$s,$id,$pe,$t,$ps,$d,$j,$rr,$a,$c)
ON CONFLICT(project_id,branch_id,seq) DO UPDATE SET id=$id,parent_event_id=$pe,type=$t,parameter_set_id=$ps,input_digest=$d,payload_json=$j,result_ref=$rr,actor=$a,created_at=$c;
""", ("$p", projectId), ("$b", e.BranchId), ("$s", e.Seq), ("$id", e.Id), ("$pe", (object?)e.ParentEventId),
            ("$t", e.Type), ("$ps", e.ParameterSetId), ("$d", e.InputDigest), ("$j", e.PayloadJson),
            ("$rr", (object?)e.ResultRef), ("$a", e.Actor), ("$c", e.CreatedAt));
    }

    public static List<LedgerEvent> ReadAll(SqliteConnection conn, string projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
SELECT branch_id,seq,id,parent_event_id,type,parameter_set_id,input_digest,payload_json,result_ref,actor,created_at
FROM events WHERE project_id=$p ORDER BY branch_id,seq;
""";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<LedgerEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LedgerEvent
            {
                BranchId = r.GetString(0),
                Seq = r.GetInt64(1),
                Id = r.GetString(2),
                ParentEventId = r.IsDBNull(3) ? null : r.GetString(3),
                Type = r.GetString(4),
                ParameterSetId = r.GetString(5),
                InputDigest = r.GetString(6),
                PayloadJson = r.GetString(7),
                ResultRef = r.IsDBNull(8) ? null : r.GetString(8),
                Actor = r.GetString(9),
                CreatedAt = r.GetDateTime(10)
            });
        return list;
    }
}
