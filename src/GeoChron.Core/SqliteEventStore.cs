using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GeoChron.Core;

public interface IEventStore
{
    EventRecord Append(string caseId, string branch, long? parentSeq, EventType type, string actor, string label,
        JsonElement payload, string? inputSummary, int? paramsVersion, IReadOnlyList<string> deps, string ruleVersion);
    EventRecord AppendConditional(string caseId, string branch, long? expectedParentSeq, EventType type, string actor,
        string label, JsonElement payload, string? inputSummary, int? paramsVersion,
        IReadOnlyList<string> deps, string ruleVersion);
    IReadOnlyList<EventRecord> ListEvents(string caseId, string? branch = null);
    IReadOnlyList<string> ListCases();
    void BeginTransaction();
    void Commit();
    void Rollback();
}

/// <summary>
/// SQLite 事件存储。所有写入在事务内完成；导出/删除/再导入保持身份、分支与决定历史。
/// 半写恢复：未提交事务在重新打开时由 SQLite 自动回滚。
/// </summary>
public sealed partial class SqliteEventStore : IEventStore, IDisposable
{
    private readonly SqliteConnection _cn;
    private SqliteTransaction? _tx;

    public SqliteEventStore(string connectionString)
    {
        _cn = new SqliteConnection(connectionString);
        _cn.Open();
        using (var pragma = _cn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        Schema();
    }

    private void Schema()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS cases(
          case_id TEXT PRIMARY KEY,
          created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS events(
          seq INTEGER NOT NULL,
          case_id TEXT NOT NULL,
          branch TEXT NOT NULL,
          event_id TEXT NOT NULL,
          type TEXT NOT NULL,
          created_at TEXT NOT NULL,
          parent_seq INTEGER,
          actor TEXT NOT NULL,
          label TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          input_summary TEXT,
          params_version INTEGER,
          deps_json TEXT NOT NULL,
          rule_version TEXT NOT NULL,
          PRIMARY KEY(case_id, branch, seq),
          UNIQUE(case_id, branch, event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_events_case ON events(case_id, branch, seq);
        CREATE TABLE IF NOT EXISTS experiments(
          experiment_id TEXT PRIMARY KEY,
          case_id TEXT NOT NULL,
          seq INTEGER NOT NULL,
          payload_json TEXT NOT NULL
        );
        """;
        cmd.ExecuteNonQuery();
    }

    public void BeginTransaction() => _tx = _cn.BeginTransaction();
    public void Commit() { _tx?.Commit(); _tx = null; }
    public void Rollback() { _tx?.Rollback(); _tx = null; }

    public EventRecord Append(string caseId, string branch, long? parentSeq, EventType type, string actor,
        string label, JsonElement payload, string? inputSummary, int? paramsVersion,
        IReadOnlyList<string> deps, string ruleVersion)
        => AppendInternal(caseId, branch, parentSeq, type, actor, label, payload, inputSummary, paramsVersion, deps, ruleVersion, null);

    public EventRecord AppendConditional(string caseId, string branch, long? expectedParentSeq, EventType type,
        string actor, string label, JsonElement payload, string? inputSummary, int? paramsVersion,
        IReadOnlyList<string> deps, string ruleVersion)
        => AppendInternal(caseId, branch, expectedParentSeq, type, actor, label, payload, inputSummary,
            paramsVersion, deps, ruleVersion, expectedParentSeq);

    private EventRecord AppendInternal(string caseId, string branch, long? parentSeq, EventType type,
        string actor, string label, JsonElement payload, string? inputSummary, int? paramsVersion,
        IReadOnlyList<string> deps, string ruleVersion, long? conditionalParent)
    {
        EnsureCase(caseId);
        var seq = NextSeq(caseId, branch);
        if (conditionalParent is not null && seq - 1 != conditionalParent.Value)
            throw new ConcurrentAppendException();
        var id = EventIds.New(type, payload.GetRawText() + seq);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = """
        INSERT INTO events(seq,case_id,branch,event_id,type,created_at,parent_seq,actor,label,
          payload_json,input_summary,params_version,deps_json,rule_version)
        VALUES($s,$c,$b,$id,$t,$at,$ps,$a,$l,$p,$sum,$pv,$deps,$rv);
        """;
        Add(cmd, "$s", seq); Add(cmd, "$c", caseId); Add(cmd, "$b", branch);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", type.ToString());
        cmd.Parameters.AddWithValue("$at", now);
        cmd.Parameters.AddWithValue("$ps", (object?)parentSeq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$p", payload.GetRawText());
        cmd.Parameters.AddWithValue("$sum", (object?)inputSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pv", (object?)paramsVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deps", JsonSerializer.Serialize(deps, CaseJson.Options));
        cmd.Parameters.AddWithValue("$rv", ruleVersion);
        cmd.ExecuteNonQuery();
        return new EventRecord(seq, id, caseId, branch, type, now, parentSeq, actor, label, payload,
            inputSummary, paramsVersion, deps, ruleVersion);
    }

    private static SqliteCommand cmd0(SqliteCommand c, string name, string val)
    { c.Parameters.AddWithValue(name, val); return c; }
    private static void Add(SqliteCommand c, string name, object val) => c.Parameters.AddWithValue(name, val);

    private long NextSeq(string caseId, string branch)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "SELECT COALESCE(MAX(seq)+1,1) FROM events WHERE case_id=$c AND branch=$b";
        cmd.Parameters.AddWithValue("$c", caseId);
        cmd.Parameters.AddWithValue("$b", branch);
        return (long)cmd.ExecuteScalar()!;
    }

    private void EnsureCase(string caseId)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = "INSERT OR IGNORE INTO cases(case_id,created_at) VALUES($c,$t)";
        cmd.Parameters.AddWithValue("$c", caseId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ListCases()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT case_id FROM cases ORDER BY case_id";
        var rows = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _cn.Dispose();
    }
}
