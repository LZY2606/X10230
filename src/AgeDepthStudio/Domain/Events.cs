namespace AgeDepthStudio.Domain;

/// <summary>
/// 机器可读事件。判定依据（单调违规、显式边界、分布保留、等优候选保留……）
/// 都以结构化 payload 落库，而不是只写日志。
/// </summary>
public sealed record StudioEvent(
    long Seq,
    string Id,
    string Type,
    string WorkspaceId,
    string At,
    string Actor,
    string PayloadJson,
    string ParentHash,
    string Hash);

/// <summary>冻结指针：模型被冻结后形成不可变版本，新锚点只能以试验方式比对。</summary>
public sealed record FreezePointer(string ModelId, string At, string Actor, string Token);

/// <summary>反例档案试验：只改动一个直接来源；采纳与否都保留。</summary>
public sealed record ArchiveTrial(
    string TrialId,
    string DirectSourceId,
    string Mutation,
    string ModelId,
    OutcomeKind Outcome,
    IReadOnlyList<string> AffectedSegmentIds,
    string ImpactScope,       // Local | WorkspaceWide
    bool Adopted,
    string JudgmentEventId,
    string At);

/// <summary>投影状态：从事件链重放得到的某一时刻完整状态。</summary>
public sealed class ProjectedState
{
    public CoreProject? Project;
    public readonly List<CoreSegment> Segments = new();
    public readonly List<SegmentGap> Gaps = new();
    public readonly List<AgeAnchor> Anchors = new();
    public readonly List<ReservoirOffset> Offsets = new();
    public readonly List<EventLayer> Layers = new();
    public readonly List<RuleParams> Params = new();
    public readonly List<Decision> Decisions = new();
    public readonly List<ModelResult> Models = new();
    public readonly List<ArchiveTrial> Trials = new();
    public string? ActiveParamsId;
    public FreezePointer? Freeze;
    public string? BaselineModelId;
    public string? CorruptUnconvertibleNote;

    public RuleParams ActiveParams =>
        Params.FirstOrDefault(p => p.ParamsId == ActiveParamsId)
        ?? Params.OrderBy(p => p.Version).Last();

    public string HeadParamsId =>
        Params.OrderBy(p => p.Version).LastOrDefault()?.ParamsId ?? "";
}

public static class ProjectedStateCloning
{
    public static ProjectedState DeepClone(this ProjectedState s)
    {
        var json = JsonSerializer.Serialize(s);
        return JsonSerializer.Deserialize<ProjectedState>(json)!;
    }
}
