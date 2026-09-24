namespace AgeDepthStudio.CasePackages;

/// <summary>
/// 离线案例包（岩芯年代尺专用领域材料，不是通用表格）。
/// steps 完整重演叙事：导入 -> 参数修订 -> 局部来源改动 -> 冲突人工决定 ->
/// 边界组合 -> 反例试验（深度重复/不整合/倒置/并列锚点/长尾/局部重算/并发冻结）。
/// </summary>
public sealed class CasePackage
{
    public string PackageId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceName { get; set; } = "";
    public string Kind { get; set; } = "narrative";
    public List<PackageStep> Steps { get; set; } = new();
}

public sealed class PackageStep
{
    public string Id { get; set; } = "";
    public string Op { get; set; } = "";
    public string Actor { get; set; } = "system";
    public string Note { get; set; } = "";
    public JsonElement Data { get; set; }
    // recompute
    public string ModelId { get; set; } = "";
    public List<string>? AffectedSegments { get; set; }
    // freeze
    public string? ExpectedParamsId { get; set; }
    // trial
    public bool Adopt { get; set; }
    public string DirectSourceId { get; set; } = "";
    public string Mutation { get; set; } = "";
    public TrialOverrideDto? Override { get; set; }
}

public sealed class TrialOverrideDto
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string? DepthRaw { get; set; }
    public double[]? Samples { get; set; }
    public string? SegmentId { get; set; }
    public int? Budget { get; set; }
}
