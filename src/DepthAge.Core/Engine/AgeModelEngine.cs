using System.Text;
using System.Text.Json;
using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

/// <summary>
/// 深度-年龄模型引擎（纯服务端，可被测试直接调用）。
/// 输出：分段、候选谱系、速率区间、不确定度带、机器可读判定与诊断。
/// </summary>
public sealed class AgeModelEngine
{
    private readonly MaterialPreparation _preparation;
    private readonly SegmentBuilder _segments;

    public AgeModelEngine(UnitRegistry? units = null)
    {
        _preparation = new MaterialPreparation(units);
        _segments = new SegmentBuilder();
    }

    public ComputationRecord Compute(ProjectState state, string? parentComputationId = null,
        List<string>? changedSourceIds = null, DateTimeOffset? now = null)
    {
        var when = now ?? DateTimeOffset.UtcNow;
        var rules = state.Rules;
        var prepared = _preparation.Prepare(state, rules);
        var segments = _segments.Build(state, prepared);
        var judgments = new List<Judgment>();
        var diagnostics = new List<Diagnostic>();

        EmitBoundaryJudgments(segments, rules, judgments, when);
        EmitMaterialDiagnostics(prepared, diagnostics);

        var computationId = StableId.Compute("comp", new
        {
            seq = state.Seq + 1,
            rules = rules.Version,
            digest = InputDigest(state)
        });

        var candidateBuilder = new CandidateBuilder();
        var candidates = candidateBuilder.Build(segments, prepared, rules, computationId, judgments, j => judgments.Add(j), when);

        // 影响范围：局部重算只标记实际依赖被改源数据的对象
        var changed = changedSourceIds ?? new List<string>();
        var impacted = ComputeImpact(segments, prepared, changed);

        var solvable = segments.Where(s => s.Solvable).ToList();
        var status = "Ok";
        if (candidateBuilder.BudgetHit)
            status = "BudgetExhausted";
        else if (solvable.Count > 0 && solvable.All(s => s.AnchorIds.Count >= 1) &&
                 solvable.Any(s => s.AnchorIds.Count >= 1 && !candidates.Any(c => c.SegmentIds.Contains(s.Id))))
            status = "SemanticImpasse";
        if (segments.Any(s => s.Inverted))
            status = status == "BudgetExhausted" ? status : "SemanticImpasse";

        var record = new ComputationRecord
        {
            Id = computationId,
            AtSeq = state.Seq + 1,
            RuleVersion = rules.Version,
            RuleBookId = rules.Id,
            InputDigest = InputDigest(state),
            ParentComputationId = parentComputationId,
            LocalRecompute = parentComputationId is not null,
            ChangedSourceIds = changed,
            ImpactedObjectIds = impacted,
            Segments = segments,
            Candidates = candidates,
            Judgments = judgments,
            Diagnostics = diagnostics,
            Status = status,
            ComputedAt = when
        };
        return record;
    }

    public static string InputDigest(ProjectState state)
    {
        string Canon(object o) => JsonSerializer.Serialize(o, EventStoreJson.Options);
        var sb = new StringBuilder();
        sb.Append("D:").Append(string.Join("|", state.Datums.Keys.OrderBy(k => k)));
        sb.Append(";S:").Append(string.Join("|", state.Sections.OrderBy(s => s.TopDepthM).ThenBy(s => s.Id).Select(s => $"{s.Id}@{s.TopDepthM}-{s.BottomDepthM}:{s.BottomBoundary}")));
        sb.Append(";A:").Append(string.Join("|", state.Anchors.Values.OrderBy(a => a.Id).Select(a =>
            $"{a.Id}:{(a.Excluded ? "X" : "A")}:{a.Depth.Quantity.RawText}:{a.Age.ReportedAge.RawText}:{a.Age.Kind}:{a.EventBedId}")));
        sb.Append(";R:").Append(string.Join("|", state.ReservoirOffsets.Values.OrderBy(r => r.Id).Select(r => $"{r.Id}={r.DeltaRYr}±{r.SigmaYr}")));
        sb.Append(";E:").Append(string.Join("|", state.EventBeds.Keys.OrderBy(k => k)));
        sb.Append(";C:").Append(string.Join("|", state.CalibrationCurves.Keys.OrderBy(k => k)));
        sb.Append(";X:").Append(string.Join("|", state.Exclusions.Where(e => !e.Revoked).OrderBy(e => e.Id).Select(e => e.TargetAnchorId)));
        sb.Append(";RB:").Append(state.Rules.Version);
        return StableId.ComputeRaw("in", sb.ToString());
    }

    private static List<string> ComputeImpact(List<ModelSegment> segments, Dictionary<string, PreparedAnchor> prepared, List<string> changed)
    {
        if (changed.Count == 0) return new();
        var impacted = new HashSet<string>();
        foreach (var id in changed)
        {
            if (prepared.TryGetValue(id, out var p))
            {
                var seg = segments.FirstOrDefault(s =>
                    p.DepthM >= s.TopDepthM - 1e-9 && p.DepthM <= s.BottomDepthM + 1e-9);
                if (seg is not null)
                {
                    impacted.UnionWith(seg.AnchorIds);
                    impacted.Add(seg.Id);
                }
            }
            else
            {
                // 段/储层偏移/事件层等：影响落在其深度范围相交的段
                foreach (var seg in segments) impacted.Add(seg.Id);
            }
        }
        return impacted.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static void EmitBoundaryJudgments(List<ModelSegment> segments, RuleBook rules, List<Judgment> judgments, DateTimeOffset now)
    {
        foreach (var segment in segments)
        {
            if (segment.LowerBoundary != SegmentBoundary.Conformable || segment.UpperBoundary != SegmentBoundary.Conformable)
            {
                var boundary = segment.LowerBoundary == SegmentBoundary.Conformable ? segment.UpperBoundary : segment.LowerBoundary;
                judgments.Add(new Judgment
                {
                    Id = StableId.Compute("judg", new { code = "J_EXPLICIT_BOUNDARY", seg = segment.Id, b = boundary }),
                    Code = "J_EXPLICIT_BOUNDARY",
                    Status = "Boundary",
                    Statement = $"{boundary} 是显式边界：{segment.BoundaryReason}；年龄只在段内单调，跨边界不插值",
                    AffectedIds = segment.SectionIds,
                    SegmentId = segment.Id,
                    DepthM = segment.BottomDepthM,
                    RuleVersion = rules.Version,
                    BasisId = string.Join(",", segment.SectionIds),
                    Attributes = new() { ["boundary"] = boundary.ToString() },
                    RecordedAt = now
                });
            }
        }
    }

    private static void EmitMaterialDiagnostics(Dictionary<string, PreparedAnchor> prepared, List<Diagnostic> outList)
    {
        foreach (var p in prepared.Values)
        foreach (var message in p.Diagnostics.Where(d => !d.StartsWith("应用储层偏移")))
        {
            outList.Add(new Diagnostic
            {
                Code = p.Ready ? "D_AGE_NOTE" : "D_MATERIAL_UNUSABLE",
                Severity = p.Ready ? "Info" : "Warning",
                Message = message,
                MaterialId = p.Anchor.Id,
                FieldPath = $"anchors[{p.Anchor.Id}]",
                DepthM = double.IsNaN(p.DepthM) ? null : p.DepthM
            });
        }
    }
}
