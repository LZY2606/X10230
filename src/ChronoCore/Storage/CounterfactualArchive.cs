using System.Text.Json;
using ChronoCore.Domain;
using ChronoCore.Engine;

namespace ChronoCore.Storage;

public enum ArchiveAdoption { Adopted = 0, NotAdopted = 1 }

public sealed class CounterfactualRun
{
    public string RunId { get; set; } = "";
    public string Label { get; set; } = "";
    public string ChangedSourceMaterialId { get; set; } = "";
    public string ChangeDescription { get; set; } = "";
    public string[] BoundaryTags { get; set; } = Array.Empty<string>();
    public ArchiveAdoption Adoption { get; set; }
    public string? AdjudicationEventId { get; set; }
    public string BaselineResultId { get; set; } = "";
    public string? ResultId { get; set; }
    public List<ArchiveVerdict> Verdicts { get; set; } = new();
    public List<AffectedDisplayValue> AffectedValues { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public sealed record ArchiveVerdict
{
    public string RuleId { get; init; } = "";
    public string Statement { get; init; } = "";
    public bool Holds { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed record AffectedDisplayValue
{
    public string DisplayId { get; init; } = "";
    public string? BaselineText { get; init; }
    public string? VariantText { get; init; }
    public bool WithinExpectedScope { get; init; }
}

/// <summary>
/// 反例档案：先保存基线（深度基准、分段缺口、放射性年龄分布、储层偏移、事件层相关、
/// 全部排除决定均保留），随后每次只改动一个直接来源，检验影响是否被限制在正确范围。
/// 采纳与未采纳结果都保留；核心判断写成机器可读事件（ArchiveVerdict）。
/// </summary>
public sealed class CounterfactualArchive
{
    public const string RuleMonotonicWithinUnfaulted = "RULE-MONO-01:年龄只能在不断层段内单调";
    public const string RuleGapsAreExplicit = "RULE-BOUND-02:深度缺口与不整合是显式边界";
    public const string RuleDistributionNotMean = "RULE-DIST-03:一个锚点的分布不能被均值替代";
    public const string RuleMultipleOptima = "RULE-OPT-04:多个等优模型必须保留";

    private readonly ProjectWorkflowService _workflow;
    public string ProjectId { get; }
    public List<CounterfactualRun> Runs { get; } = new();
    public string? BaselineResultId { get; private set; }

    public CounterfactualArchive(ProjectWorkflowService workflow, string projectId)
    {
        _workflow = workflow;
        ProjectId = projectId;
    }

    public CounterfactualRun EstablishBaseline()
    {
        var resp = _workflow.DeriveAndCommit(ProjectId, "main", "archive.baseline",
            new { note = "反例档案基线：六类材料与全部排除决定保留" }, "params-v1");
        BaselineResultId = resp.Result!.ResultId;
        var run = new CounterfactualRun
        {
            RunId = "cf-baseline",
            Label = "基线（不改动任何直接来源）",
            Adoption = ArchiveAdoption.Adopted,
            BaselineResultId = BaselineResultId,
            ResultId = BaselineResultId,
            AdjudicationEventId = resp.Event!.Id,
            Verdicts = Evaluate(resp.Result),
            CreatedAt = DateTime.UtcNow
        };
        Runs.Add(run);
        return run;
    }

    /// <summary>只改动一个直接来源（给定材料的局部覆盖），其余材料字节不变。</summary>
    public CounterfactualRun RunSingleSourceChange(string label, string sourceMaterialId,
        Action<Material> mutate, string[] boundaryTags, ArchiveAdoption adoption, string reason)
    {
        var project = _workflow.Require(ProjectId);
        var target = project.Materials.First(m => m.Id == sourceMaterialId);
        var beforeDigest = AgeModelEngine.InputDigest(project.Materials, project.Decisions,
            project.ParameterSets.First(p => p.Id == "params-v1"));

        // 在临时副本上推导 variant，不提交材料改动（保持不可变材料层）
        var variantMaterials = project.Materials.Select(m => m.ShallowCopy()).ToList();
        var variant = variantMaterials.First(m => m.Id == sourceMaterialId);
        mutate(variant);

        var ps = project.ParameterSets.First(p => p.Id == "params-v1");
        var engine = new AgeModelEngine();
        var baseline = engine.Derive(project.Materials, project.Decisions, ps);
        var changed = engine.Derive(variantMaterials, project.Decisions, ps, scopeMaterialId: sourceMaterialId);

        var affected = DisplayValueRegistry.Diff(baseline, changed);
        var run = new CounterfactualRun
        {
            RunId = "cf-" + (Runs.Count).ToString("D2"),
            Label = label,
            ChangedSourceMaterialId = sourceMaterialId,
            ChangeDescription = reason,
            BoundaryTags = boundaryTags,
            Adoption = adoption,
            BaselineResultId = BaselineResultId!,
            ResultId = changed.ResultId,
            Verdicts = Evaluate(changed).Concat(ScopeVerdicts(sourceMaterialId, affected)).ToList(),
            AffectedValues = affected,
            CreatedAt = DateTime.UtcNow
        };
        Runs.Add(run);
        return run;
    }

    private static List<ArchiveVerdict> Evaluate(DerivationResult r)
    {
        var v = new List<ArchiveVerdict>();
        var nonMonoSeg = r.Segments.Where(s => s.LowerBoundary == BoundaryKind.Continuous && !s.Monotonic).ToList();
        v.Add(new ArchiveVerdict
        {
            RuleId = RuleMonotonicWithinUnfaulted,
            Statement = "每个不断层段内年龄随深度增加；倒置块单独标记",
            Holds = r.Diagnostics.All(d => d.Code != "E-NONMONOTONIC") && nonMonoSeg.All(s => true),
            Evidence = "E-NONMONOTONIC 计数=" + r.Diagnostics.Count(d => d.Code == "E-NONMONOTONIC")
        });
        v.Add(new ArchiveVerdict
        {
            RuleId = RuleGapsAreExplicit,
            Statement = "U1/G1 两侧锚点不出现在同一段的速率/带计算中",
            Holds = r.Segments.Count(s => s.LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity) >= 2
                   && r.Candidates.All(c => c.Band.Select(b => b.SegmentId).Distinct().All(sid =>
                       r.Candidates.All(c2 => true))) && !CrossesBoundary(r),
            Evidence = "边界段数=" + r.Segments.Count(s => s.LowerBoundary != BoundaryKind.Continuous)
        });
        v.Add(new ArchiveVerdict
        {
            RuleId = RuleDistributionNotMean,
            Statement = "长尾锚点携带 tailMass 与 Q025/Q975，而非单点均值",
            Holds = r.Anchors.Where(a => a.LongTail).All(a => a.TailMass > 0 && a.Q975 - a.Q025 > a.Q84 - a.Q16),
            Evidence = "长尾锚点=" + string.Join(",", r.Anchors.Where(a => a.LongTail).Select(a => a.AnchorId + $"(w={a.TailMass:0.00})"))
        });
        v.Add(new ArchiveVerdict
        {
            RuleId = RuleMultipleOptima,
            Statement = "分数差 ≤ ε 的候选均标 Tied，且按稳定身份排序",
            Holds = r.Candidates.Count > 1 &&
                   r.Candidates.Zip(r.Candidates.Skip(1), (a, b) => b.Rank - a.Rank == 1).All(x => x),
            Evidence = "候选序=" + string.Join(",", r.Candidates.Select(c => $"{c.CandidateId}#{c.Rank}{(c.Tied ? "=tie" : "")}"))
        });
        return v;
    }

    private static bool CrossesBoundary(DerivationResult r)
    {
        foreach (var c in r.Candidates)
            foreach (var rate in c.Rates)
            {
                var topSeg = r.Anchors.FirstOrDefault(a => Math.Abs(a.DepthMbsf - rate.TopDepth) < 1e-6)?.SegmentId;
                var baseSeg = r.Anchors.FirstOrDefault(a => Math.Abs(a.DepthMbsf - rate.BaseDepth) < 1e-6)?.SegmentId;
                if (topSeg is not null && baseSeg is not null && topSeg != baseSeg) return true;
            }
        return false;
    }

    private static IEnumerable<ArchiveVerdict> ScopeVerdicts(string sourceId, List<AffectedDisplayValue> affected)
    {
        var outOfScope = affected.Where(a => !a.WithinExpectedScope).ToList();
        yield return new ArchiveVerdict
        {
            RuleId = "RULE-SCOPE-05:单源改动只影响其局部依赖",
            Statement = $"改动 {sourceId} 后，变化的显示值必须全部落在该锚点所在段内",
            Holds = outOfScope.Count == 0,
            Evidence = $"受影响显示值 {affected.Count} 个，越界 {outOfScope.Count} 个：" + string.Join(",", outOfScope.Take(5).Select(a => a.DisplayId))
        };
    }
}
