using System.Text.Json;

namespace AgeDepth.Core;

public sealed record CaseStepReport(
    int Index, string Op, string Note, long[] EventSeqs, string Outcome);

public sealed record CaseReplayReport(
    string CaseId, string Title, List<CaseStepReport> Steps, List<string> ExpectFailures)
{
    public bool AllExpectationsMet => ExpectFailures.Count == 0;
}

/// <summary>
/// 离线案例包执行器：按叙事顺序执行导入、参数修订、局部源改动、
/// 冲突决定、边界情形触发与冻结，并验证 expect 断言。
/// </summary>
public static class CaseRunner
{
    public static CaseReplayReport Run(Engine engine, string caseJson)
    {
        using var doc = JsonDocument.Parse(caseJson);
        var root = doc.RootElement;
        var caseId = root.GetProperty("id").GetString()!;
        var title = root.GetProperty("title").GetString()!;
        var steps = new List<CaseStepReport>();
        var failures = new List<string>();
        var i = 0;

        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            var op = step.GetProperty("op").GetString()!;
            var note = step.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            var seqs = new List<long>();
            var outcome = "ok";
            var before = engine.HeadSeq;

            switch (op)
            {
                case "import":
                {
                    var r = engine.ImportMaterialsJson(step.GetProperty("materials").GetRawText());
                    outcome = r.GetType().Name;
                    break;
                }
                case "revise-param":
                {
                    var p = step.GetProperty("params");
                    var next = new ModelParams(
                        p.GetProperty("draws").GetInt32(),
                        p.GetProperty("gridPoints").GetInt32())
                    { Version = p.GetProperty("version").GetString()! };
                    engine.ReviseParams(next, note);
                    break;
                }
                case "source-change":
                {
                    // 只影响局部依赖的源数据改动：新增/替换一个锚点 → 局部重算
                    var anchorJson = $$"""{"anchors":[{{step.GetProperty("anchor").GetRawText()}}]}""";
                    var bundle = MaterialsJson.Parse(anchorJson);
                    var report = engine.AddAnchor(bundle.Anchors[0]);
                    outcome = report.Summary;
                    break;
                }
                case "decide":
                {
                    engine.Decide(step.GetProperty("kind").GetString()!,
                        step.GetProperty("targetId").GetString()!,
                        step.GetProperty("verdict").GetString()!,
                        step.GetProperty("rationale").GetString()!);
                    break;
                }
                case "conflict":
                {
                    // 互相冲突的人工决定：两条都记录，历史完整保留
                    foreach (var d in step.GetProperty("decisions").EnumerateArray())
                        engine.Decide(d.GetProperty("kind").GetString()!,
                            d.GetProperty("targetId").GetString()!,
                            d.GetProperty("verdict").GetString()!,
                            d.GetProperty("rationale").GetString()!);
                    break;
                }
                case "freeze":
                {
                    var r = engine.Freeze(ResolveCandidate(engine, step.GetProperty("candidate").GetString()!),
                        engine.HeadSeq);
                    outcome = r.GetType().Name;
                    break;
                }
                case "freeze-stale":
                {
                    // 并发冻结检验：用过期序列号冻结，必须被拒绝并留痕
                    var candId = ResolveCandidate(engine, step.GetProperty("candidate").GetString()!);
                    var staleSeq = Math.Max(0, engine.HeadSeq - step.GetProperty("staleBy").GetInt32());
                    var r = engine.Freeze(candId, staleSeq);
                    outcome = r.GetType().Name;
                    break;
                }
                case "expect":
                {
                    var state = engine.State();
                    foreach (var check in step.GetProperty("checks").EnumerateArray())
                    {
                        var what = check.GetProperty("check").GetString()!;
                        var fail = Check(state, engine, what, check);
                        if (fail is not null) failures.Add($"step[{i}] {what}: {fail}");
                    }
                    outcome = failures.Count == 0 ? "ok" : "见 ExpectFailures";
                    break;
                }
            }

            for (var s = before + 1; s <= engine.HeadSeq; s++) seqs.Add(s);
            steps.Add(new CaseStepReport(i, op, note, seqs.ToArray(), outcome));
            i++;
        }
        return new CaseReplayReport(caseId, title, steps, failures);
    }

    private static string ResolveCandidate(Engine engine, string target)
    {
        var state = engine.State();
        if (target.StartsWith("best-in:", StringComparison.Ordinal))
        {
            var seg = target["best-in:".Length..];
            return state.CandidatesFor(seg).OrderByDescending(c => c.Score)
                .ThenBy(c => c.Id, StringComparer.Ordinal).First().Id;
        }
        if (target == "best")
            return state.Candidates.OrderByDescending(c => c.Score)
                .ThenBy(c => c.Id, StringComparer.Ordinal).First().Id;
        return target;
    }

    private static string? Check(ReviewState state, Engine engine, string what, JsonElement check)
    {
        switch (what)
        {
            case "diagnostic-present":
            {
                var code = check.GetProperty("code").GetString()!;
                return state.Diagnostics.Any(d => d.Code == code) ? null : $"缺少诊断 {code}";
            }
            case "min-candidates":
            {
                var seg = check.GetProperty("segment").GetString()!;
                var min = check.GetProperty("min").GetInt32();
                var n = state.CandidatesFor(seg).Count();
                return n >= min ? null : $"层段 {seg} 候选数 {n} < {min}";
            }
            case "equal-best-kept":
            {
                var seg = check.GetProperty("segment").GetString()!;
                var n = state.CandidatesFor(seg).Count(c => c.Status == "equal-best");
                return n >= 2 ? null : $"层段 {seg} 等优候选数 {n} < 2";
            }
            case "frozen":
                return state.FrozenCandidateId is not null ? null : "没有冻结模型";
            case "conflict-recorded":
                return state.Conflicts.Count > 0 ? null : "没有并发冲突记录";
            case "samples-preserved":
            {
                var anchorId = check.GetProperty("anchor").GetString()!;
                var a = state.Materials.Anchors.FirstOrDefault(x => x.Id == anchorId);
                return a?.Age.Samples is { Length: > 0 } ? null : $"锚点 {anchorId} 的分布样本被丢弃";
            }
            case "local-recompute-only":
            {
                var seg = check.GetProperty("segment").GetString()!;
                var last = engine.Events().LastOrDefault(e => e.Kind == EventKinds.ModelComputed);
                if (last is null) return "没有 model.computed 事件";
                var affected = last.PayloadObj().GetProperty("affectedSegments").Deserialize<string[]>()!;
                return affected.SequenceEqual(new[] { seg })
                    ? null : $"最近重算影响范围 [{string.Join(",", affected)}] 超出 {seg}";
            }
            case "event-kind-present":
            {
                var kind = check.GetProperty("kind").GetString()!;
                return engine.Events().Any(e => e.Kind == kind) ? null : $"缺少事件 {kind}";
            }
            default:
                return $"未知断言 {what}";
        }
    }
}
