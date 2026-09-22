using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoChron.Core;

/// <summary>一次只改动一个直接来源（反例档案的扰动也走同一受控入口）。</summary>
public sealed record SourceChangePayload(
    [property: JsonPropertyName("changeId")] string ChangeId,
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("oldRaw")] string? OldRaw,
    [property: JsonPropertyName("newRaw")] string? NewRaw,
    [property: JsonPropertyName("newUnit")] string? NewUnit,
    [property: JsonPropertyName("reason")] string Reason)
{
    public string DependencyRef => $"{SubjectKind.ToLowerInvariant()}:{SubjectId}";
}

/// <summary>互相冲突/任何人工决定：接受、否定、参数调整都生成新事件。</summary>
public sealed record DecisionPayload(
    [property: JsonPropertyName("decisionId")] string DecisionId,
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("action")] string Action, // accept | reject | param-adjust | prefer-candidate
    [property: JsonPropertyName("targetCandidateId")] string? TargetCandidateId,
    [property: JsonPropertyName("rationale")] string Rationale)
{
    public string SubjectRef => $"{SubjectKind.ToLowerInvariant()}:{SubjectId}";
}

/// <summary>
/// 材料“修改”通过追加新事件实现；此投影器在重放时生成新材料快照。
/// 不可变层：深度基准/分段/缺口/锚点/储层/事件层/排除决定始终可追溯。
/// </summary>
public static class SourceMutator
{
    public static CasePack WithParams(CasePack pack, ModelParams p) => pack with { Params = p };

    public static List<Diagnostic> Validate(CasePack pack, SourceChangePayload c)
    {
        var diags = new List<Diagnostic>();
        if (string.IsNullOrWhiteSpace(c.SubjectId))
            diags.Add(new Diagnostic("M001", "subjectId", "源数据改动必须指定唯一对象标识"));
        if (c.NewRaw is null && c.NewUnit is null)
            diags.Add(new Diagnostic("M002", "newRaw", "改动必须提供新原始表示或新单位；不能用空值伪装成功"));
        var exists = c.SubjectKind.ToLowerInvariant() switch
        {
            "anchor" => pack.Anchors.Any(a => a.Id == c.SubjectId),
            "segment" => pack.Segments.Any(s => s.Id == c.SubjectId),
            "gap" => pack.Gaps.Any(g => g.Id == c.SubjectId),
            "event" => pack.EventLayers.Any(e => e.Id == c.SubjectId),
            "reservoir" => pack.ReservoirOffsets.Any(r => r.Id == c.SubjectId),
            "datum" => pack.DepthDatum.Id == c.SubjectId,
            _ => false
        };
        if (!exists)
            diags.Add(new Diagnostic("M003", $"{c.SubjectKind}:{c.SubjectId}", "被改动对象在当前材料中不存在"));
        return diags;
    }

    public static CasePack Apply(CasePack pack, SourceChangePayload c)
    {
        return c.SubjectKind.ToLowerInvariant() switch
        {
            "anchor" => pack with { Anchors = pack.Anchors.Select(a => a.Id == c.SubjectId ? PatchAnchor(a, c) : a).ToList() },
            "segment" => pack with { Segments = pack.Segments.Select(s => s.Id == c.SubjectId ? PatchSegment(s, c) : s).ToList() },
            "gap" => pack with { Gaps = pack.Gaps.Select(g => g.Id == c.SubjectId ? PatchGap(g, c) : g).ToList() },
            "event" => pack with { EventLayers = pack.EventLayers.Select(e => e.Id == c.SubjectId ? PatchEvent(e, c) : e).ToList() },
            "reservoir" => pack with { ReservoirOffsets = pack.ReservoirOffsets.Select(r => r.Id == c.SubjectId ? PatchReservoir(r, c) : r).ToList() },
            "datum" => pack with { DepthDatum = PatchDatum(pack.DepthDatum, c) },
            _ => throw new InvalidOperationException($"未知对象类型 {c.SubjectKind}")
        };
    }

    private static Anchor PatchAnchor(Anchor a, SourceChangePayload c) => c.Field switch
    {
        "depth" => a with { Depth = UnitConversions.Depth(c.NewRaw, c.NewUnit ?? a.Depth.Unit) },
        _ => throw new InvalidOperationException($"锚点暂不支持字段 {c.Field} 的局部改动（用人工决定/排除事件表达）")
    };

    private static Segment PatchSegment(Segment s, SourceChangePayload c) => c.Field switch
    {
        "topDepth" => s with { TopDepth = UnitConversions.Depth(c.NewRaw, c.NewUnit ?? s.TopDepth.Unit) },
        "bottomDepth" => s with { BottomDepth = UnitConversions.Depth(c.NewRaw, c.NewUnit ?? s.BottomDepth.Unit) },
        _ => throw new InvalidOperationException($"分段暂不支持字段 {c.Field}")
    };

    private static Gap PatchGap(Gap g, SourceChangePayload c) => c.Field switch
    {
        "atOrBelowDepth" => g with { AtOrBelowDepth = UnitConversions.Depth(c.NewRaw, c.NewUnit ?? g.AtOrBelowDepth.Unit) },
        _ => throw new InvalidOperationException($"缺口暂不支持字段 {c.Field}")
    };

    private static EventLayer PatchEvent(EventLayer e, SourceChangePayload c) => c.Field switch
    {
        "depth" => e with { Depth = UnitConversions.Depth(c.NewRaw, c.NewUnit ?? e.Depth.Unit) },
        _ => throw new InvalidOperationException($"事件层暂不支持字段 {c.Field}")
    };

    private static ReservoirOffset PatchReservoir(ReservoirOffset r, SourceChangePayload c) => c.Field switch
    {
        "value" => r with { Value = UnitConversions.Age(c.NewRaw, c.NewUnit ?? r.AgeUnit), AgeUnit = c.NewUnit ?? r.AgeUnit },
        _ => throw new InvalidOperationException($"储层偏移暂不支持字段 {c.Field}")
    };

    private static DepthDatum PatchDatum(DepthDatum d, SourceChangePayload c) => c.Field switch
    {
        "depthUnit" => d with { DepthUnit = c.NewUnit! },
        _ => throw new InvalidOperationException($"深度基准暂不支持字段 {c.Field}")
    };

    public static CasePack ApplyDecision(CasePack pack, DecisionPayload d)
    {
        var id = d.DecisionId;
        var exclusion = new ExclusionDecision(id, d.SubjectId, d.SubjectKind,
            d.Action switch
            {
                "reject" => $"人工否定：{d.Rationale}",
                "accept" => $"人工接受：{d.Rationale}",
                _ => d.Rationale
            },
            "reviewer", d.Action);
        // 冲突决定也保留：同一对象出现 accept 与 reject 时两条排除决定都留在谱系中。
        var exclusions = pack.Exclusions.Where(e => e.Id != id).Append(exclusion).ToList();
        var anchors = pack.Anchors.Select(a =>
        {
            if (a.Id != d.SubjectId || !d.SubjectKind.Equals("anchor", StringComparison.OrdinalIgnoreCase)) return a;
            return d.Action switch
            {
                "reject" => a with { Status = "excluded", ExclusionReason = d.Rationale },
                "accept" => a with { Status = "active", ExclusionReason = null },
                _ => a
            };
        }).ToList();
        return pack with { Exclusions = exclusions, Anchors = anchors };
    }
}
