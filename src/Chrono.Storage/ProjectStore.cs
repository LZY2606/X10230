using System.Text.Json;
using Chrono.Core.Events;
using Microsoft.Data.Sqlite;

namespace Chrono.Storage;

public enum AppendFailure { None, ConcurrentWrite, BranchFrozen, BranchMissing }

public sealed record AppendOutcome(AppendFailure Failure, ProjectEvent? Event)
{
    public bool Ok => Failure == AppendFailure.None;
}

public sealed class ProjectStore
{
    private readonly string _connectionString;

    public ProjectStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema.Sql;
        cmd.ExecuteNonQuery();
    }

    public void PutProjectMeta(string projectId, string name, string createdAtUtc)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx,
            "INSERT OR IGNORE INTO projects(id,name,created_at_utc) VALUES($id,$name,$ts)",
            ("$id", projectId), ("$name", name), ("$ts", createdAtUtc));
        Execute(conn, tx,
            "INSERT OR IGNORE INTO branches(id,project_id,parent_branch_id,created_at_utc,frozen) " +
            "VALUES($id,$pid,NULL,$ts,0)",
            ("$id", "main"), ("$pid", projectId), ("$ts", createdAtUtc));
        tx.Commit();
    }

    public bool ProjectExists()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public string GetProjectId()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects ORDER BY rowid LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    public long NextSequence(string branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM events WHERE branch_id=$b";
        cmd.Parameters.AddWithValue("$b", branchId);
        return (long)cmd.ExecuteScalar()!;
    }

    public AppendOutcome Append(ProjectEvent e, long expectedSequence, IReadOnlyList<string> frozenCandidatesBlockedOn)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        if (!BranchExists(conn, tx, e.BranchId))
        {
            tx.Rollback();
            return new AppendOutcome(AppendFailure.BranchMissing, null);
        }
        if (IsBranchFrozen(conn, tx, e.BranchId))
        {
            tx.Rollback();
            return new AppendOutcome(AppendFailure.BranchFrozen, null);
        }

        long actual;
        using (var seq = conn.CreateCommand())
        {
            seq.Transaction = tx;
            seq.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events WHERE branch_id=$b";
            seq.Parameters.AddWithValue("$b", e.BranchId);
            actual = (long)seq.ExecuteScalar()!;
        }
        if (actual + 1 != expectedSequence)
        {
            tx.Rollback();
            return new AppendOutcome(AppendFailure.ConcurrentWrite, null);
        }

        InsertEvent(conn, tx, e);
        tx.Commit();
        return new AppendOutcome(AppendFailure.None, e);
    }

    public string CreateBranch(string branchId, string projectId, string fromEventId, string reason, string actor, string ruleSet, DateTimeOffset now)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long maxSeq;
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events WHERE project_id=$p AND branch_id='main'";
            c.Parameters.AddWithValue("$p", projectId);
            maxSeq = (long)c.ExecuteScalar()!;
        }
        Execute(conn, tx,
            "INSERT OR IGNORE INTO branches(id,project_id,parent_branch_id,created_at_utc,frozen) " +
            "VALUES($id,$p,'main',$ts,0)",
            ("$id", branchId), ("$p", projectId), ("$ts", now.ToString("O")));
        var evt = ProjectEvent.Create(
            StableIds.Event(projectId, "branch", branchId), projectId, branchId, maxSeq + 1,
            new[] { fromEventId }, EventKind.BranchCreated,
            new BranchPayload(branchId, fromEventId, reason), actor, ruleSet, now, $"branch:{branchId}");
        InsertEvent(conn, tx, evt);
        tx.Commit();
        return evt.Id;
    }

    public void FreezeBranch(string branchId, bool frozen = true)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, "UPDATE branches SET frozen=$f WHERE id=$b",
            ("$f", frozen ? 1L : 0L), ("$b", branchId));
        tx.Commit();
    }

    public bool IsFrozen(string branchId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        return IsBranchFrozen(conn, tx, branchId);
    }

    public IReadOnlyList<string> ListBranches()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM branches ORDER BY rowid";
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<ProjectEvent> ReadEvents(string branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + EventColumns + " FROM events WHERE branch_id=$b ORDER BY sequence";
        cmd.Parameters.AddWithValue("$b", branchId);
        return ReadEventList(cmd);
    }

    public List<ProjectEvent> ReadAllEvents(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + EventColumns + " FROM events WHERE project_id=$p ORDER BY branch_id, sequence";
        cmd.Parameters.AddWithValue("$p", projectId);
        return ReadEventList(cmd);
    }

    public void SaveMaterialSnapshot(string eventId, string branchId, long sequence, string payloadJson)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx,
            "INSERT OR REPLACE INTO material_snapshots(event_id,branch_id,sequence,payload_json) VALUES($e,$b,$s,$p)",
            ("$e", eventId), ("$b", branchId), ("$s", sequence), ("$p", payloadJson));
        tx.Commit();
    }

    public string? ReadMaterialSnapshot(string branchId, long sequence)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM material_snapshots WHERE branch_id=$b AND sequence=$s";
        cmd.Parameters.AddWithValue("$b", branchId);
        cmd.Parameters.AddWithValue("$s", sequence);
        return cmd.ExecuteScalar() as string;
    }

    public void SaveDerivedCache(string eventId, string branchId, long sequence, string payloadJson)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx,
            "INSERT OR REPLACE INTO derived_cache(event_id,branch_id,sequence,payload_json) VALUES($e,$b,$s,$p)",
            ("$e", eventId), ("$b", branchId), ("$s", sequence), ("$p", payloadJson));
        tx.Commit();
    }

    public string? ReadDerivedCache(string branchId, long sequence)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM derived_cache WHERE branch_id=$b AND sequence=$s";
        cmd.Parameters.AddWithValue("$b", branchId);
        cmd.Parameters.AddWithValue("$s", sequence);
        return cmd.ExecuteScalar() as string;
    }

    private const string EventColumns =
        "id,project_id,branch_id,sequence,parent_event_ids,kind,payload_json,actor,rule_set_version,created_at_utc,label,frozen";

    private static List<ProjectEvent> ReadEventList(SqliteCommand cmd)
    {
        var result = new List<ProjectEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ProjectEvent
            {
                Id = reader.GetString(0),
                ProjectId = reader.GetString(1),
                BranchId = reader.GetString(2),
                Sequence = reader.GetInt64(3),
                ParentEventIds = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? Array.Empty<string>(),
                Kind = Enum.Parse<EventKind>(reader.GetString(5)),
                PayloadJson = reader.GetString(6),
                Actor = reader.GetString(7),
                RuleSetVersion = reader.GetString(8),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(9)),
                Label = reader.IsDBNull(10) ? null : reader.GetString(10),
                Frozen = reader.GetInt64(11) != 0
            });
        }
        return result;
    }

    private static void InsertEvent(SqliteConnection conn, SqliteTransaction tx, ProjectEvent e)
    {
        Execute(conn, tx,
            "INSERT INTO events(id,project_id,branch_id,sequence,parent_event_ids,kind,payload_json," +
            "actor,rule_set_version,created_at_utc,label,frozen) VALUES(" +
            "$id,$p,$b,$s,$par,$k,$pl,$a,$r,$ts,$l,$f)",
            ("$id", e.Id), ("$p", e.ProjectId), ("$b", e.BranchId), ("$s", e.Sequence),
            ("$par", JsonSerializer.Serialize(e.ParentEventIds)),
            ("$k", e.Kind.ToString()), ("$pl", e.PayloadJson),
            ("$a", e.Actor), ("$r", e.RuleSetVersion),
            ("$ts", e.CreatedAtUtc.ToString("O")),
            ("$l", (object?)e.Label ?? DBNull.Value),
            ("$f", e.Frozen ? 1L : 0L));
    }

    private static bool BranchExists(SqliteConnection conn, SqliteTransaction tx, string branchId)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT COUNT(*) FROM branches WHERE id=$b";
        c.Parameters.AddWithValue("$b", branchId);
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    private static bool IsBranchFrozen(SqliteConnection conn, SqliteTransaction tx, string branchId)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT frozen FROM branches WHERE id=$b";
        c.Parameters.AddWithValue("$b", branchId);
        return Convert.ToInt64(c.ExecuteScalar()!) != 0;
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.Parameters.AddWithValue(name, value is DBNull ? DBNull.Value : value);
            if (value is string) p.SqliteType = SqliteType.Text;
        }
        cmd.ExecuteNonQuery();
    }
}

public static class StableIds
{
    public static string Event(string projectId, string kind, string discriminator)
    {
        var material = $"{projectId}|{kind}|{discriminator}";
        return "evt-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }
}
