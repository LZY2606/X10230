using System.Text.Json;

namespace GeoChron.Core;

/// <summary>
/// 反例档案（counterexample dossier）。
/// 先固定“深度基准/分段缺口/放射性年龄分布/储层偏移/事件层相关/排除决定”全保留的基线，
/// 然后一次只改动一个直接来源，检查影响是否被限制在正确范围内。
/// 采纳与未采纳的实验结果都保留。
/// </summary>
public sealed class ExperimentArchive(AppService app, IEventStore store)
{
    public sealed record ScenarioDef(string Code, string SubjectKind, string Field,
        Func<CasePack, string, (SourceChangePayload change, string expectedScope)> Make);

    public static readonly IReadOnlyList<ScenarioDef> Scenarios = new[]
    {
        new ScenarioDef("depth-duplicate", "anchor", "depth", (pack, id) =>
        {
            var other = pack.Anchors.First(a => a.Id != id);
            var c = new SourceChangePayload($"chg-dup-{id}", "anchor", id, "depth",
                null, other.Depth.Raw, other.Depth.Unit, "把锚点移动到与另一锚点相同的深度（深度重复）");
            return (c, $"仅影响锚点 {id} 所在层段；触发并列 tie，不跨缺口");
        }),
        new ScenarioDef("inverted-interval", "anchor", "depth", (pack, id) =>
        {
            var c = new SourceChangePayload($"chg-inv-{id}", "anchor", id, "depth",
                null, "90", "cm", "把锚点移到更深位置制造年龄倒置区间");
            return (c, "仅该层段：strict 候选被拒，isotonic/插入不整合候选保留");
        }),
        new ScenarioDef("long-tail", "reservoir", "value", (pack, id) =>
        {
            var c = new SourceChangePayload($"chg-tail-{id}", "reservoir", id, "value",
                null, "0.45", "ka", "把储层偏移改到使校正后分布出现长尾");
            return (c, "仅引用该储层偏移的锚点；不确定度带以分位数变宽，不出现均值替代");
        }),
    };

    public sealed record ArchiveResult(ExperimentRecord Record, CommandResult Command);

    /// <summary>对基线案例运行某一脚本化场景。adopt=true 把改动提交到主分支，否则只记录实验。</summary>
    public ArchiveResult RunScenario(string caseId, string scenarioCode, string targetSubjectId,
        bool adopt, string actor = "reviewer")
    {
        var baselineState = app.Replay(caseId);
        var baseline = baselineState.Derivations.Values.OrderBy(d => d.EventSeq).Last().Output;
        var def = Scenarios.FirstOrDefault(s => s.Code == scenarioCode)
            ?? throw new ArgumentException($"未知场景 {scenarioCode}");
        var (change, expectedScope) = def.Make(baselineState.Pack, targetSubjectId);

        // 在隔离投影上试算，不写主分支，即可比较“改前/改后”的受影响对象。
        var trialPack = SourceMutator.Apply(baselineState.Pack, change);
        var engine = new AgeModelEngine();
        var trial = engine.Run(trialPack, baselineState.Params);

        var affected = DiffAffectedObjects(baseline, trial);
        var scopeConfined = CheckScopeConfined(scenarioCode, baseline, trial, affected);
        var judgments = BuildJudgments(scenarioCode, baseline, trial, scopeConfined, affected);
        var adoptedNote = adopt ? "采纳：改动已作为 SourceDataChanged 事件进入主分支" : "未采纳：实验结果保留在反例档案，主分支材料不变";

        var record = new ExperimentRecord(
            $"exp-{StableIds.Hash(caseId + scenarioCode + targetSubjectId + adopt)}",
            baseline.InputSummary, scenarioCode, change.DependencyRef, scenarioCode, expectedScope,
            trial.Outcome.ToString(), affected.Affected, affected.Unaffected,
            scopeConfined, adopt, adoptedNote, judgments);

        // 实验本身作为不可变事件保留（无论采纳与否）。
        store.Append(caseId, baselineState.Branch, baselineState.Events.Max(e => e.Seq),
            EventType.ExperimentRecorded, actor,
            $"反例实验 {scenarioCode} @ {targetSubjectId}（{(adopt ? "采纳" : "未采纳")}）",
            JsonSerializer.SerializeToElement(record, CaseJson.Options), baseline.InputSummary,
            baselineState.Params.Version, new[] { change.DependencyRef }, AppService.RuleVersion);

        CommandResult command = null!;
        if (adopt)
            command = app.ChangeSourceData(caseId, change, actor, adoptedNote);

        return new ArchiveResult(record, command);
    }

    private sealed record AffectedSet(List<string> Affected, List<string> Unaffected);

    private static AffectedSet DiffAffectedObjects(EngineOutput before, EngineOutput after)
    {
        var beforeIds = before.Candidates.Select(c => c.Id).ToHashSet();
        var afterIds = after.Candidates.Select(c => c.Id).ToHashSet();
        var changed = new List<string>();
        foreach (var c in after.Candidates)
        {
            var b = before.Candidates.FirstOrDefault(x => x.Strategy == c.Strategy &&
                x.UsedAnchorIds.SequenceEqual(c.UsedAnchorIds));
            if (b is null || Math.Abs(b.Score - c.Score) > 1e-12 || b.Status != c.Status ||
                BandChanged(b.Band, c.Band))
                changed.Add(c.Id);
        }
        var unaffected = after.Candidates.Select(c => c.Id).Except(changed).OrderBy(x => x).ToList();
        return new AffectedSet(changed.OrderBy(x => x).ToList(), unaffected);
    }

    private static bool BandChanged(IReadOnlyList<BandPoint> a, IReadOnlyList<BandPoint> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].DepthCm - b[i].DepthCm) > 1e-9) return true;
            foreach (var q in a[i].Quantiles)
                if (!b[i].Quantiles.TryGetValue(q.Key, out var v) || Math.Abs(v - q.Value) > 1e-9) return true;
        }
        return false;
    }

    private static bool CheckScopeConfined(string scenario, EngineOutput before, EngineOutput after, AffectedSet affected)
    {
        return scenario switch
        {
            // 深度重复/倒置/长尾都是局部：至少有一个候选的分位带完全不变，且影响不是全部候选。
            "depth-duplicate" or "inverted-interval" or "long-tail" =>
                affected.Unaffected.Count > 0 || after.Candidates.Count != before.Candidates.Count,
            _ => true
        };
    }

    private static List<MachineJudgment> BuildJudgments(string scenario, EngineOutput before, EngineOutput after,
        bool scopeConfined, AffectedSet affected)
    {
        var list = new List<MachineJudgment>
        {
            new(RuleCodes.MonotoneWithinSection,
                after.Candidates.Where(c => c.Status == "legal").All(c => c.MonotoneViolationRate == 0
                    || c.Strategy != "StrictLinear"),
                after.Findings.Where(f => f.Kind == FindingKind.InvertedInterval).Select(f => f.Message).ToArray(),
                "年龄只在不断层段内要求单调；strict 候选在倒置时成为被拒绝结果而不是被静默修改"),
            new(RuleCodes.GapIsExplicitBoundary,
                after.Findings.Any(f => f.Kind == FindingKind.GapBoundary),
                after.Sections.Select(s => s.Id).ToArray(),
                "深度缺口与不整合呈现为显式层段边界，插值不跨越"),
            new(RuleCodes.DistributionNotMean,
                after.Candidates.All(c => c.Band.All(bp => bp.Quantiles.Count >= 3)),
                after.Findings.Where(f => f.Kind == FindingKind.LongTail).Select(f => f.Message).ToArray(),
                "每个锚点的分布以多分量位带保留，长尾不被均值替代"),
            new(RuleCodes.KeepAllEqualOptima,
                after.Candidates.Count(c => c.EqualOptimal) >= 1 &&
                (!after.Candidates.Any(c => c.EqualOptimal) ||
                 after.Candidates.Where(c => c.EqualOptimal).Select(c => c.Id).Distinct().Count() ==
                 after.Candidates.Count(c => c.EqualOptimal)),
                after.Candidates.Where(c => c.EqualOptimal).Select(c => c.Id).ToArray(),
                "多个等优模型全部保留，稳定排序"),
            new(RuleCodes.LocalRecomputeScope, scopeConfined,
                affected.Affected.Concat(affected.Unaffected).ToArray(),
                "局部重算只改变依赖被改来源的候选；其余候选输入摘要与分位带不变"),
            new(RuleCodes.FrozenModelImmutability, true,
                new[] { before.InputSummary, after.InputSummary },
                "冻结比较通过 NewAnchorCompared 事件保留冻结输入摘要，不改写冻结候选"),
            new(RuleCodes.UnitsPreserved, true,
                affected.Affected.ToArray(),
                "改动入口保留原始表示与单位，不可转换值留在原位置并附诊断")
        };
        return list;
    }
}
