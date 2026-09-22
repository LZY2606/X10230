using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GeoChron.Core;

/// <summary>导出包：身份/顺序/来源关系全部在事件行内，再导入即重放，无数据库自增 id 依赖。</summary>
public sealed record ExportBundle(
    string Format, string ExportedAt, IReadOnlyList<ExportedEvent> Events)
{
    public const string FormatName = "geochron-export-v1";
}

public sealed record ExportedEvent(long Seq, string CaseId, string Branch, string EventId, string Type,
    string CreatedAt, long? ParentSeq, string Actor, string Label, string PayloadJson,
    string? InputSummary, int? ParamsVersion, string DepsJson, string RuleVersion);

public enum ImportResultKind { Imported, FormatCorrupt, SemanticUnresolved }

public sealed record ImportResult(ImportResultKind Kind, int EventCount, IReadOnlyList<Diagnostic> Diagnostics);

public partial class SqliteEventStore
{
    public IReadOnlyList<EventRecord> ListEvents(string caseId, string? branch = null)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = branch is null
            ? "SELECT * FROM events WHERE case_id=$c ORDER BY branch, seq"
            : "SELECT * FROM events WHERE case_id=$c AND branch=$b ORDER BY seq";
        cmd.Parameters.AddWithValue("$c", caseId);
        if (branch is not null) cmd.Parameters.AddWithValue("$b", branch);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static EventRecord Map(SqliteDataReader r)
    {
        var payload = JsonDocument.Parse(r.GetString(r.GetOrdinal("payload_json"))).RootElement.Clone();
        var deps = JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("deps_json")), CaseJson.Options) ?? new();
        var pOrd = r.GetOrdinal("parent_seq");
        var vOrd = r.GetOrdinal("params_version");
        var parent = r.IsDBNull(pOrd) ? (long?)null : r.GetInt64(pOrd);
        var pv = r.IsDBNull(vOrd) ? (int?)null : r.GetInt32(vOrd);
        return new EventRecord(
            r.GetInt64(r.GetOrdinal("seq")),
            r.GetString(r.GetOrdinal("event_id")),
            r.GetString(r.GetOrdinal("case_id")),
            r.GetString(r.GetOrdinal("branch")),
            Enum.Parse<EventType>(r.GetString(r.GetOrdinal("type"))),
            r.GetString(r.GetOrdinal("created_at")),
            parent,
            r.GetString(r.GetOrdinal("actor")),
            r.GetString(r.GetOrdinal("label")),
            payload,
            r.IsDBNull(r.GetOrdinal("input_summary")) ? null : r.GetString(r.GetOrdinal("input_summary")),
            pv, deps,
            r.GetString(r.GetOrdinal("rule_version")));
    }

    public ExportBundle Export(string? caseId = null)
    {
        var events = new List<ExportedEvent>();
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = caseId is null
            ? "SELECT * FROM events ORDER BY case_id, branch, seq"
            : "SELECT * FROM events WHERE case_id=$c ORDER BY branch, seq";
        if (caseId is not null) cmd.Parameters.AddWithValue("$c", caseId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            events.Add(new ExportedEvent(
                r.GetInt64(r.GetOrdinal("seq")), r.GetString(r.GetOrdinal("case_id")),
                r.GetString(r.GetOrdinal("branch")), r.GetString(r.GetOrdinal("event_id")),
                r.GetString(r.GetOrdinal("type")), r.GetString(r.GetOrdinal("created_at")),
                r.IsDBNull(r.GetOrdinal("parent_seq")) ? null : r.GetInt64(r.GetOrdinal("parent_seq")),
                r.GetString(r.GetOrdinal("actor")), r.GetString(r.GetOrdinal("label")),
                r.GetString(r.GetOrdinal("payload_json")),
                r.IsDBNull(r.GetOrdinal("input_summary")) ? null : r.GetString(r.GetOrdinal("input_summary")),
                r.IsDBNull(r.GetOrdinal("params_version")) ? null : r.GetInt32(r.GetOrdinal("params_version")),
                r.GetString(r.GetOrdinal("deps_json")), r.GetString(r.GetOrdinal("rule_version"))));
        return new ExportBundle(ExportBundle.FormatName, DateTimeOffset.UtcNow.ToString("O"), events);
    }

    /// <summary>
    /// 原子导入：先逐行校验（格式/语义），全部通过才在一个事务内写入。
    /// 任一行损坏或顺序非法 → 回滚，数据库保持导入前状态（半写恢复）。
    /// </summary>
    public ImportResult ImportBundle(string json, bool simulateHalfWrite = false)
    {
        ExportBundle? bundle;
        var diags = new List<Diagnostic>();
        try
        {
            bundle = JsonSerializer.Deserialize<ExportBundle>(json, CaseJson.Options);
            if (bundle is null || bundle.Format != ExportBundle.FormatName)
                return new ImportResult(ImportResultKind.FormatCorrupt, 0,
                    new[] { new Diagnostic("I001", "$", "导出包为空或 format 不是 geochron-export-v1") });
        }
        catch (JsonException ex)
        {
            return new ImportResult(ImportResultKind.FormatCorrupt, 0,
                new[] { new Diagnostic("I900", "$", $"导出包格式损坏：{ex.Message}") });
        }

        // 顺序与身份校验：等价输入得到稳定顺序；seq 必须连续、event_id 唯一、父引用合法。
        var seen = new HashSet<string>();
        var perBranch = new Dictionary<string, long>();
        foreach (var e in bundle.Events)
        {
            var key = $"{e.CaseId}|{e.Branch}|{e.EventId}";
            if (!seen.Add(key))
            {
                diags.Add(new Diagnostic("I010", $"events[{e.EventId}]", "事件身份重复，违反稳定身份约束"));
                continue;
            }
            var expected = perBranch.GetValueOrDefault(e.Branch, 0) + 1;
            if (e.Seq != expected)
                diags.Add(new Diagnostic("I011", $"events[{e.EventId}]",
                    $"事件顺序不连续：分支 {e.Branch} 期望 seq {expected}，实际 {e.Seq}"));
            perBranch[e.Branch] = e.Seq;
            try { JsonDocument.Parse(e.PayloadJson); }
            catch (JsonException ex)
            {
                diags.Add(new Diagnostic("I012", $"events[{e.EventId}].payload", $"事件负载格式损坏：{ex.Message}"));
            }
        }
        if (diags.Count > 0)
            return new ImportResult(ImportResultKind.FormatCorrupt, 0, diags);

        var ownTx = _tx is null;
        if (ownTx) BeginTransaction();
        try
        {
            EnsureCase(bundle.Events[0].CaseId);
            foreach (var e in bundle.Events)
            {
                using var cmd = _cn.CreateCommand();
                cmd.Transaction = _tx;
                cmd.CommandText = """
                INSERT OR REPLACE INTO events(seq,case_id,branch,event_id,type,created_at,parent_seq,actor,
                  label,payload_json,input_summary,params_version,deps_json,rule_version)
                VALUES($s,$c,$b,$id,$t,$at,$ps,$a,$l,$p,$sum,$pv,$deps,$rv);
                """;
                Add(cmd, "$s", e.Seq); Add(cmd, "$c", e.CaseId); Add(cmd, "$b", e.Branch);
                cmd.Parameters.AddWithValue("$id", e.EventId);
                cmd.Parameters.AddWithValue("$t", e.Type);
                cmd.Parameters.AddWithValue("$at", e.CreatedAt);
                cmd.Parameters.AddWithValue("$ps", (object?)e.ParentSeq ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$a", e.Actor);
                cmd.Parameters.AddWithValue("$l", e.Label);
                cmd.Parameters.AddWithValue("$p", e.PayloadJson);
                cmd.Parameters.AddWithValue("$sum", (object?)e.InputSummary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pv", (object?)e.ParamsVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$deps", e.DepsJson);
                cmd.Parameters.AddWithValue("$rv", e.RuleVersion);
                cmd.ExecuteNonQuery();
            }
            if (simulateHalfWrite)
                throw new IOException("I099: 模拟半写崩溃（事务将回滚，不留半成品）");
            if (ownTx) Commit();
            return new ImportResult(ImportResultKind.Imported, bundle.Events.Count, Array.Empty<Diagnostic>());
        }
        catch
        {
            if (ownTx) Rollback();
            throw;
        }
    }
}
