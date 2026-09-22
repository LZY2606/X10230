using System.Text.Json;

namespace GeoChron.Core;

/// <summary>一条可用于拟合的年龄约束（锚点或事件层）。</summary>
public sealed record Constraint(
    string Id, string Label, double DepthCm, IAgeDistribution Distribution,
    string Kind, bool Excludable, string SourceRef, string? ReservoirOffsetId);

public sealed record PreparedConstraints(
    IReadOnlyList<Constraint> All,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> ActiveIds);

public static class ConstraintPreparer
{
    public static PreparedConstraints Prepare(CasePack pack, IReadOnlyList<Section> sections)
    {
        var diags = new List<Diagnostic>();
        var findings = new List<Finding>();
        var list = new List<Constraint>();
        var excludedIds = new HashSet<string>();

        var offsets = pack.ReservoirOffsets.ToDictionary(o => o.Id);
        foreach (var ex in pack.Exclusions)
        {
            excludedIds.Add(ex.SubjectId);
            findings.Add(new Finding(FindingKind.ExcludedAnchor, new[] { ex.SubjectId }, null, null,
                $"{ex.SubjectKind} {ex.SubjectId} 已排除：{ex.Reason}（决定人 {ex.DecidedBy}，依据 {ex.Basis}）"));
        }

        void Add(string id, string label, Quantity depth, AgeDistributionInfo dist, string kind,
            bool excludable, string sourceRef, string? reservoirId)
        {
            if (!depth.IsConvertible)
            {
                diags.Add(new Diagnostic("C001", $"{kind}[{id}].depth",
                    $"深度不可转换：'{depth.Raw}' {depth.Unit}（{depth.ConversionDiagnostic}），值保留在原位置，该约束不参与推导"));
                findings.Add(new Finding(FindingKind.NonConvertibleValue, new[] { id }, null, null,
                    $"{label} 深度无法规范化，保留原始表示"));
                return;
            }
            var parsed = DistributionParser.Build(dist);
            if (!parsed.Valid)
            {
                foreach (var d in parsed.Diagnostics)
                    diags.Add(new Diagnostic(d.Code, $"{kind}[{id}].ageDistribution.{d.Path}", d.Message));
                findings.Add(new Finding(FindingKind.NonConvertibleValue, new[] { id }, null, depth.Canonical,
                    $"{label} 年龄分布无法构造，原始 JSON 片段保留，不以均值替代"));
                return;
            }
            if (excludedIds.Contains(id)) { /* 仍构造，但标记；策略可决定是否使用 */ }
            var section = sections.SectionAt(depth.Canonical!.Value);
            if (section is null)
            {
                diags.Add(new Diagnostic("C002", $"{kind}[{id}].depth",
                    $"深度 {depth.Raw} {depth.Unit} 不落在任何连续层段内（位于缺口/岩芯范围外），不参与层段内插值"));
            }
            if (excludable && reservoirId is not null)
            {
                if (offsets.TryGetValue(reservoirId, out var off))
                {
                    if (off.Value.IsConvertible)
                        findings.Add(new Finding(FindingKind.ReservoirApplied, new[] { id, off.Id },
                            section?.Id, depth.Canonical!.Value,
                            $"储层偏移 {off.Label} = {off.Value.Raw} {off.AgeUnit} 已施加于 {label}"));
                    else
                        diags.Add(new Diagnostic("C003", $"reservoirOffsets[{off.Id}]", "储层偏移值不可转换，保留原始表示"));
                }
                else
                    diags.Add(new Diagnostic("C004", $"anchors[{id}].reservoirOffsetId", $"引用了不存在的储层偏移 {reservoirId}"));
            }
            list.Add(new Constraint(id, label, depth.Canonical!.Value, parsed, kind, excludable, sourceRef, reservoirId));
        }

        foreach (var a in pack.Anchors)
            Add(a.Id, a.Label, a.Depth, a.Distribution, "anchor", true, a.SampleRef, a.ReservoirOffsetId);
        foreach (var ev in pack.EventLayers)
            Add(ev.Id, ev.Label, ev.Depth, ev.AgeDistribution ?? throw new JsonException($"事件层 {ev.Id} 缺少 ageDistribution"),
                "event", false, ev.SourceRef, null);

        var inSection = list.Where(c => sections.SectionAt(c.DepthCm) is not null).ToList();
        foreach (var grp in inSection.GroupBy(c => c.DepthCm).Where(g => g.Count() > 1))
        {
            var ids = grp.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            findings.Add(new Finding(FindingKind.DepthDuplicate, ids,
                sections.SectionAt(grp.Key)?.Id, grp.Key,
                $"深度重复：{grp.Count()} 个约束位于同一深度 {grp.Key:0.##} cm（{string.Join(", ", ids)}），按并列锚点处理"));
        }
        var active = inSection.Where(c => !excludedIds.Contains(c.Id)).Select(c => c.Id).ToList();
        return new PreparedConstraints(list, diags, findings, active);
    }

    /// <summary>把储层偏移（ka）叠加到样本上：放射性年龄先做储层校正。</summary>
    public static double[] ApplyReservoir(double[] agesKa, CasePack pack, Constraint c)
    {
        if (c.ReservoirOffsetId is null) return agesKa;
        var off = pack.ReservoirOffsets.FirstOrDefault(o => o.Id == c.ReservoirOffsetId);
        if (off?.Value.IsConvertible != true) return agesKa;
        var r = off.Value.Canonical!.Value; // ka
        return agesKa.Select(a => a - r).ToArray();
    }
}
