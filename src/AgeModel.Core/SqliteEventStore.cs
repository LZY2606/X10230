using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AgeModel.Core;

/// <summary>
/// SQLite 事件存储。所有提交在单事务内完成；暂存区用于“半写恢复”验证：
/// 模拟进程在提交前崩溃时，Recover() 丢弃未提交片段且主库完整。
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;

    public SqliteEventStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        using var conn = Open();
        Init(conn);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Init(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
              project_id TEXT NOT NULL,
              branch TEXT NOT NULL,
              seq INTEGER NOT NULL,
              id TEXT NOT NULL,
              type TEXT NOT NULL,
              parent_event_id TEXT,
              payload TEXT NOT NULL,
              actor TEXT NOT NULL,
              occurred_at TEXT NOT NULL,
              input_summary TEXT,
              parameter_version TEXT,
              hash TEXT NOT NULL,
              parent_hash TEXT,
              PRIMARY KEY(project_id, branch, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_events_project ON events(project_id);
            CREATE TABLE IF NOT EXISTS write_staging(
              staging_id TEXT PRIMARY KEY,
              created_at TEXT NOT NULL,
              project_id TEXT NOT NULL,
              payload_json TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Append(StoredEvent ev)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events(project_id, branch, seq, id, type, parent_event_id, payload,
              actor, occurred_at, input_summary, parameter_version, hash, parent_hash)
            VALUES(@project_id,@branch,@seq,@id,@type,@parent_event_id,@payload,
              @actor,@occurred_at,@input_summary,@parameter_version,@hash,@parent_hash);
            """;
        Bind(ev, cmd);
        try
        {
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // seq 由调用方做乐观并发控制；主键冲突只可能来自并发竞争。
            throw new ConcurrencyConflictException(ev.Seq - 1, ev.Seq - 1);
        }
    }

    private static void Bind(StoredEvent ev, SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("@project_id", ProjectFromPayloadOrEvent(ev));
        cmd.Parameters.AddWithValue("@branch", ev.Branch);
        cmd.Parameters.AddWithValue("@seq", ev.Seq);
        cmd.Parameters.AddWithValue("@id", ev.Id);
        cmd.Parameters.AddWithValue("@type", ev.Type);
        cmd.Parameters.AddWithValue("@parent_event_id", (object?)ev.ParentEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@payload", ev.Payload.GetRawText());
        cmd.Parameters.AddWithValue("@actor", ev.Actor);
        cmd.Parameters.AddWithValue("@occurred_at", ev.OccurredAt);
        cmd.Parameters.AddWithValue("@input_summary", (object?)ev.InputSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parameter_version", (object?)ev.ParameterVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", ev.Hash);
        cmd.Parameters.AddWithValue("@parent_hash", (object?)ev.ParentHash ?? DBNull.Value);
    }

    private static string ProjectFromPayloadOrEvent(StoredEvent ev)
        => ev.Payload.TryGetProperty("projectId", out var v) ? v.GetString() ?? "default" : "default";

    public IReadOnlyList<StoredEvent> GetEvents(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM events WHERE project_id=@p ORDER BY branch, seq";
        cmd.Parameters.AddWithValue("@p", projectId);
        var list = new List<StoredEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public long HeadSeq(string projectId, string branch)
    {
        using var conn = Open();
        return HeadSeq(conn, projectId, branch);
    }

    private static long HeadSeq(SqliteConnection conn, string projectId, string branch)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events WHERE project_id=@p AND branch=@b";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@b", branch);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<string> ListProjects()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT project_id FROM events ORDER BY project_id";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void DeleteProject(string projectId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM events WHERE project_id=@p; DELETE FROM write_staging WHERE project_id=@p;";
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>显式制造“半写”：先把有效载荷放入暂存区，不提交主表。</summary>
    public void StageIncompleteWrite(string stagingId, string projectId, string payloadJson)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO write_staging(staging_id, created_at, project_id, payload_json) VALUES(@s,@t,@p,@j)";
        cmd.Parameters.AddWithValue("@s", stagingId);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@j", payloadJson);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ListStaging()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT staging_id FROM write_staging";
        var l = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    }

    public int Recover()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var countCmd = conn.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*) FROM write_staging";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM write_staging";
        del.ExecuteNonQuery();
        tx.Commit();
        return count;
    }

    private static StoredEvent Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Seq = r.GetInt64(r.GetOrdinal("seq")),
        Type = r.GetString(r.GetOrdinal("type")),
        Branch = r.GetString(r.GetOrdinal("branch")),
        ParentEventId = r.IsDBNull(r.GetOrdinal("parent_event_id")) ? null : r.GetString(r.GetOrdinal("parent_event_id")),
        Payload = JsonDocument.Parse(r.GetString(r.GetOrdinal("payload"))).RootElement.Clone(),
        Actor = r.GetString(r.GetOrdinal("actor")),
        OccurredAt = r.GetString(r.GetOrdinal("occurred_at")),
        InputSummary = r.IsDBNull(r.GetOrdinal("input_summary")) ? null : r.GetString(r.GetOrdinal("input_summary")),
        ParameterVersion = r.IsDBNull(r.GetOrdinal("parameter_version")) ? null : r.GetString(r.GetOrdinal("parameter_version")),
        Hash = r.GetString(r.GetOrdinal("hash")),
        ParentHash = r.IsDBNull(r.GetOrdinal("parent_hash")) ? null : r.GetString(r.GetOrdinal("parent_hash")),
    };

    public void Dispose() { }
}
