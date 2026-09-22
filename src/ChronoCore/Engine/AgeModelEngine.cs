using ChronoCore.Domain;
using System.Diagnostics;

namespace ChronoCore.Engine;

/// <summary>
/// 深度-年龄模型引擎。规则只在服务端实现：
/// 1) 年龄只能在不断层段内单调；缺口/不整合/断层为显式边界，绝不跨边界插值；
/// 2) 锚点保留完整经验分布（伽马上尾），均值仅用于展示，不替代分布；
/// 3) 并列分数的候选全部保留，且顺序稳定可复现。
/// </summary>
public sealed partial class AgeModelEngine
{
    public const string RulesetVersion = "rules-2026.09.1";

    private readonly Stopwatch _clock = new();
    private int _drawsUsed;
    private bool _budgetHit;

    public DerivationResult Derive(
        IReadOnlyList<Material> materials,
        IReadOnlyList<Decision> decisions,
        ParameterSet parameters,
        string branchId = "main",
        string? parentResultId = null,
        string? scopeMaterialId = null)
    {
        _clock.Restart();
        _drawsUsed = 0;
        _budgetHit = false;
        var diagnostics = new List<DiagnosticEntry>();
        var result = new DerivationResult
        {
            ResultId = "res_" + Guid.NewGuid().ToString("N")[..12],
            ParameterSetId = parameters.Id,
            RulesetVersion = RulesetVersion,
            ParentResultId = parentResultId,
            ScopeMaterialId = scopeMaterialId,
            ComputedAt = DateTime.UtcNow
        };
        result.ParentMaterialIds.AddRange(materials.Select(m => m.Id).Order());
        result.InputDigest = InputDigest(materials, decisions, parameters);

        foreach (var m in materials) CollectMeasurementDiagnostics(m, diagnostics);

        var active = ApplyDecisions(materials, decisions, diagnostics);

        var segments = BuildSegments(active, diagnostics);
        result.Segments = segments.Select(ToView).ToList();

        var blocking = diagnostics.Where(d => d.Severity == Severity.Error
            && d.Code is "E-MISSING" or "E-CORRUPT" or "E-CONFLICT").ToList();
        if (blocking.Any(d => d.Location == "segments" || d.Code == "E-CORRUPT"))
        {
            result.Outcome = EngineOutcome.InputIncomplete;
            return Finish(result, diagnostics);
        }

        var anchors = BuildAnchors(active, segments, parameters, diagnostics);
        result.Anchors = anchors;

        if (diagnostics.Exists(d => d.Code == "E-UNCONFORMITY-CROSS") ||
            (diagnostics.Exists(d => d.Code == "E-INVERTED") && anchors.Count == 0))
        {
            result.Outcome = EngineOutcome.SemanticallyUnsolvable;
            return Finish(result, diagnostics);
        }
        if (segments.All(s => s.AnchorCount == 0))
        {
            diagnostics.Add(new DiagnosticEntry { Code = "E-NO-ANCHORS", Severity = Severity.Error, Location = "model", Message = "没有任何可定位锚点，语义上无解" });
            result.Outcome = EngineOutcome.SemanticallyUnsolvable;
            return Finish(result, diagnostics);
        }

        var candidates = BuildCandidates(anchors, segments, parameters, diagnostics, scopeMaterialId);
        result.Candidates = candidates;
        result.DrawsUsed = _drawsUsed;
        result.Outcome = _budgetHit ? EngineOutcome.BudgetExhausted : EngineOutcome.Ok;
        if (_budgetHit)
            diagnostics.Add(new DiagnosticEntry { Code = "W-BUDGET", Severity = Severity.Warning, Location = "engine", Message = "蒙特卡洛资源预算耗尽：返回已收敛部分并显式标记，区别于语义无解" });
        return Finish(result, diagnostics);
    }

    private DerivationResult Finish(DerivationResult r, List<DiagnosticEntry> d)
    {
        _clock.Stop();
        r.BudgetTicksUsed = _clock.ElapsedTicks;
        r.Diagnostics = d;
        return r;
    }

    public static string InputDigest(IReadOnlyList<Material> materials, IReadOnlyList<Decision> decisions, ParameterSet p)
    {
        var mats = Hashing.StableDigest(materials, m =>
            string.Join("|", m.Id, m.Kind, m.SourceRef, m.SourceLine,
                m.DepthRaw?.Raw, m.AgeRaw?.Raw, m.OneSigmaRaw?.Raw,
                m.DeltaR?.Raw, m.EventDepthRaw?.Raw, m.EventAgeRaw?.Raw,
                m.BoundaryAtBase, m.OverturnedSection, m.Distribution ?? "", m.ShapeK, m.ScaleTheta,
                m.MinClamp, m.Excluded, m.ExclusionReason ?? ""), "materials-v1");
        var decs = Hashing.StableDigest(decisions, d =>
            string.Join("|", d.Id, d.Kind, d.Status, d.TargetMaterialId ?? "", d.ParameterSetId, d.PayloadJson, d.SupersedesDecisionId ?? ""), "decisions-v1");
        var ps = Hashing.Sha256(new { p.Id, p.Version, p.RulesetVersion, p.MonteCarloDraws, p.ApplyReservoirToMarine, p.KeepReworkedTail, p.TiedScoreEpsilon });
        return Hashing.Sha256(new { mats, decs, ps, engine = RulesetVersion });
    }

    private static SegmentView ToView(SegmentCtx s) => new()
    {
        SegmentId = s.SegmentId,
        TopDepth = s.Top,
        BaseDepth = s.Base,
        LowerBoundary = s.LowerBoundary,
        Lithology = s.Lithology,
        Monotonic = !s.Overturned
    };

    private static List<Material> ApplyDecisions(IReadOnlyList<Material> all, IReadOnlyList<Decision> decisions, List<DiagnosticEntry> diag)
    {
        var byTarget = decisions.Where(d => d.Status == DecisionStatus.Accepted && d.TargetMaterialId is not null)
            .GroupBy(d => d.TargetMaterialId!).ToDictionary(g => g.Key, g => g.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToList());
        var active = new List<Material>();
        foreach (var m in all)
        {
            var copy = m.ShallowCopy();
            if (byTarget.TryGetValue(m.Id, out var list))
            {
                foreach (var d in list)
                {
                    if (d.Kind == DecisionKind.Exclude) { copy.Excluded = true; copy.ExclusionReason ??= d.Reason; copy.ExclusionEventId = d.EventId; }
                    if (d.Kind == DecisionKind.Reinstate) { copy.Excluded = false; copy.ExclusionReason = null; copy.ExclusionEventId = null; }
                    if (d.Kind == DecisionKind.ManualOverride && d.Status == DecisionStatus.Conflict)
                        diag.Add(new DiagnosticEntry { Code = "E-CONFLICT", Severity = Severity.Error, Location = $"decision/{d.Id}", MaterialId = m.Id, Message = $"互相冲突的人工决定未消解: {d.Reason}" });
                }
            }
            active.Add(copy);
        }
        return active;
    }
}
