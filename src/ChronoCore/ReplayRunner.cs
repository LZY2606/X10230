using System.Text.Json;

namespace ChronoCore;

public sealed record ReplayStepResult(
    ReplayScenario Scenario,
    string BranchId,
    string Outcome,                // semantic-impossible | success-contained | budget-exhausted
    string Determination,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> EvidenceEventIds);

/// <summary>反例档案回放：保存基线 → 只改一个直接来源 → 检验影响范围。</summary>
public sealed class ReplayRunner
{
    private static readonly JsonSerializerOptions Jo = new() { PropertyNameCaseInsensitive = true };
    private readonly ChronoAppService _app;
    private readonly DerivationEngine _engine = new();

    public ReplayRunner(ChronoAppService app) { _app = app; }

    public ReplayStepResult Run(ReplayScenario scenario, ProjectState baseline,
        ParameterSet parameters, string branchId, long parentSeq, string baselineDigest)
    {
        using var doc = JsonDocument.Parse(scenario.InputFragmentJson);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString()!;
        var evidence = new List<string>();
        var diags = new List<Diagnostic>();
        var affected = new List<string>();
        var outcome = "success-contained";
        var determination = "contained";

        var trial = CloneState(baseline);
        trial.BranchId = branchId;

        switch (kind)
        {
            case "anchor":
            case "add-anchor":
            {
                var anchor = root.GetProperty("anchor").Deserialize<Anchor>(
                    new JsonSerializerOptions(Jo)
                    {
                        Converters = { new FragmentSamplesConverter() }
                    })!;
                var depthQ = Quantity.Depth(anchor.DepthRaw, anchor.DatumId);
                trial.Anchors[anchor.Id] = anchor;
                if (!trial.AnchorOrder.Any(a => a.Id == anchor.Id)) trial.AnchorOrder.Add(anchor);

                var derive = _engine.Derive(trial, parameters);
                diags.AddRange(derive.Diagnostics);
                outcome = derive.Kind switch
                {
                    OutcomeKind.SemanticallyImpossible => "semantic-impossible",
                    OutcomeKind.BudgetExhausted => "budget-exhausted",
                    _ => "success-contained"
                };
                determination = outcome switch
                {
                    "semantic-impossible" => "semantic-impossible",
                    "budget-exhausted" => "budget-exhausted",
                    _ => "contained"
                };
                affected.AddRange(AffectedFromDiagnostics(derive.Diagnostics));
                if (derive.Kind == OutcomeKind.Success)
                {
                    var best = derive.Candidates.OrderBy(m => m.FitScore).First();
                    var report = _engine.CompareNewAnchor(baseline, best, anchor, null, parameters);
                    affected.AddRange(report.AffectedIntervalIds);
                    diags.AddRange(report.Diagnostics);
                    if (!report.MonotonicPreserved) outcome = "semantic-impossible";
                }
                break;
            }
            case "anchor-revise":
            {
                var id = root.GetProperty("anchorId").GetString()!;
                var original = baseline.Anchors[id];
                var samples = ParseSamplesSpec(root.GetProperty("samples").GetString()!);
                var revised = original with { Samples = samples, Note = (original.Note ?? "") + " [局部重算版本]" };
                trial.Anchors[id] = revised;
                trial.AnchorOrder[trial.AnchorOrder.FindIndex(a => a.Id == id)] = revised;
                var scope = root.TryGetProperty("scopeIntervalId", out var sc)
                    ? new[] { sc.GetString()! } : null;
                var derive = _engine.Derive(trial, parameters, scope);
                diags.AddRange(derive.Diagnostics);
                affected.AddRange(scope ?? Array.Empty<string>());
                // 内容寻址：未改动段模型 Id 与基线一致，只有受影响段出现新 Id
                var baseModels = _engine.Derive(baseline, parameters, scope).Candidates;
                var unchanged = scope is null ? 0 :
                    derive.Candidates.Select(m => string.Join("|", m.Envelope
                        .Where(e => !scope.Contains(e.IntervalId))
                        .Select(e => e.MedianAge))).Intersect(
                        baseModels.Select(m => string.Join("|", m.Envelope
                        .Where(e => !scope.Contains(e.IntervalId))
                        .Select(e => e.MedianAge)))).Count();
                determination = unchanged > 0 ? "contained" : "leaked";
                evidence.Add("digest:" + derive.InputDigest);
                break;
            }
            case "parameter":
            {
                var p = parameters;
                if (root.TryGetProperty("maxCandidates", out var mc))
                    p = p with { MaxCandidates = mc.GetInt32() };
                var derive = _engine.Derive(trial, p);
                diags.AddRange(derive.Diagnostics);
                outcome = derive.Kind == OutcomeKind.BudgetExhausted ? "budget-exhausted" : "success-contained";
                if (derive.Kind == OutcomeKind.Success)
                {
                    var ties = derive.Candidates.Count(m => m.Tie);
                    affected.AddRange(derive.Candidates.Where(m => m.Tie).Select(m => m.Id));
                    determination = ties >= 2 ? "retained" : "contained";
                }
                else determination = "budget-exhausted";
                break;
            }
            case "inspect":
            {
                var id = root.GetProperty("anchorId").GetString()!;
                var an = baseline.Anchors[id];
                var w95 = an.Samples.Q975 - an.Samples.Q025;
                var w99 = an.Samples.Q995 - an.Samples.Q005;
                affected.Add(id);
                diags.Add(new Diagnostic("J-LONGTAIL",
                    $"{id} 95%宽={w95:0} cal a，99%宽={w99:0} cal a（99% 更宽 => 长尾被保留）",
                    id, "samples", null, DiagSeverity.Info));
                determination = w99 > w95 ? "retained" : "leaked";
                break;
            }
            case "freeze":
            {
                var holder = root.GetProperty("holderActor").GetString()!;
                var contender = root.GetProperty("contenderActor").GetString()!;
                var rank = root.GetProperty("modelRank").GetInt32();
                var derive = _engine.Derive(trial, parameters);
                var target = derive.Candidates.OrderBy(m => m.FitScore).Skip(rank - 1).First();
                affected.Add(target.Id);
                var ok1 = _app.TryFreeze(branchId, target.Id, derive.InputDigest, parameters.Version,
                    holder, out var c1, out var h1);
                if (c1 is not null) evidence.Add(c1.Id);
                var ok2 = _app.TryFreeze(branchId, target.Id, derive.InputDigest, parameters.Version,
                    contender, out var c2, out var h2);
                if (c2 is not null) evidence.Add(c2.Id);
                outcome = (!ok2 && h2 == holder) ? "success-contained" : "semantic-impossible";
                determination = "retained";
                diags.Add(new Diagnostic("J-FREEZE",
                    $"首次冻结 {(ok1 ? "成功" : "失败")}；并发冻结被 {(ok2 ? "错误接受" : "拒绝并记录冲突")}",
                    target.Id, null, null,
                    ok2 ? DiagSeverity.Error : DiagSeverity.Info));
                break;
            }
            default:
                throw new InvalidOperationException("未知回放片段种类 " + kind);
        }

        var holds = outcome == "success-contained";
        var judgment = new JudgmentPayload(scenario.Scenario, scenario.PropositionCode, holds,
            determination,
            JsonSerializer.Serialize(new { affected, scope = scenario.ExpectScope }),
            evidence, new[] { "fragment:" + scenario.Scenario },
            scenario.InputFragmentJson, scenario.ExpectDetail);
        _app.RecordJudgment(branchId, judgment);
        evidence.Add("judgment:" + scenario.Scenario);

        return new ReplayStepResult(scenario, branchId, outcome, determination,
            affected, diags, evidence);
    }

    private static IEnumerable<string> AffectedFromDiagnostics(IEnumerable<Diagnostic> ds)
    {
        foreach (var d in ds)
            if (d.MaterialId is not null) yield return d.MaterialId;
            else if (d.Location is not null) yield return d.Location;
    }

    public static AgeSamples ParseSamplesSpec(string spec)
    {
        // rebuild:normal:mean:sd | rebuild:student-t:loc:scale:df | rebuild:empirical:...
        var parts = spec.Split(':');
        return parts[1] switch
        {
            "normal" => SamplesFactory.Normal(double.Parse(parts[2]), double.Parse(parts[3])),
            "student-t" => SamplesFactory.StudentT(double.Parse(parts[2]), double.Parse(parts[3]), int.Parse(parts[4])),
            _ => SamplesFactory.Empirical(parts.Skip(2).Select(double.Parse))
        };
    }

    public static ProjectState CloneState(ProjectState s)
    {
        var c = new ProjectState
        {
            ProjectId = s.ProjectId, Name = s.Name, BranchId = s.BranchId,
            BaselineDigest = s.BaselineDigest, ChangedSourceId = s.ChangedSourceId,
            HeadSeq = s.HeadSeq, CurrentParameterVersion = s.CurrentParameterVersion
        };
        foreach (var x in s.DatumOrder) { c.Datums[x.Id] = x; c.DatumOrder.Add(x); }
        foreach (var x in s.IntervalOrder) { c.Intervals[x.Id] = x; c.IntervalOrder.Add(x); }
        foreach (var x in s.GapOrder) { c.Gaps[x.Id] = x; c.GapOrder.Add(x); }
        foreach (var x in s.AnchorOrder) { c.Anchors[x.Id] = x; c.AnchorOrder.Add(x); }
        foreach (var x in s.ReservoirOrder) { c.Reservoirs[x.Id] = x; c.ReservoirOrder.Add(x); }
        foreach (var x in s.EventOrder) { c.Events[x.Id] = x; c.EventOrder.Add(x); }
        foreach (var x in s.ExclusionOrder) { c.Exclusions[x.Id] = x; c.ExclusionOrder.Add(x); }
        foreach (var x in s.ParameterOrder) { c.Parameters[x.Version] = x; c.ParameterOrder.Add(x); }
        foreach (var x in s.DecisionOrder) { c.Decisions[x.Id] = x; c.DecisionOrder.Add(x); }
        foreach (var kv in s.Derived) c.Derived[kv.Key] = kv.Value;
        return c;
    }
}
