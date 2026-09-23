using System.Text.Json.Nodes;

namespace ChronoCore;

// 事件类型常量
public static class EventType
{
    public const string ProjectCreated = "project.created";
    public const string MaterialImported = "material.imported";
    public const string MaterialRevised = "material.revised";       // 参数修订/源数据改动
    public const string MaterialExcluded = "material.excluded";
    public const string ParameterRevised = "parameter.revised";
    public const string DecisionMade = "decision.made";
    public const string ModelsDerived = "models.derived";
    public const string ModelFrozen = "model.frozen";
    public const string FreezeConflict = "freeze.conflict";         // 并发冻结冲突
    public const string ImpactCompared = "impact.compared";         // 冻结后比较新锚点
    public const string BranchForked = "branch.forked";
    public const string ReplayJudgment = "replay.judgment";         // 机器可读取判定依据
    public const string BudgetExhausted = "derive.budget-exhausted";
    public const string FormatCorrupted = "import.format-corrupted";
    public const string SemanticImpossible = "derive.semantic-impossible";
    public const string LocalRecompute = "derive.local-recompute";
    public const string HalfWriteRecovered = "storage.halfwrite-recovered";
}

/// <summary>
/// 事件溯源事件：结构化状态与可重放证据。回看任意事件即折叠到当时依赖版本与可见结论，
/// 绝不用最新结果覆盖历史。
/// </summary>
public sealed record ChronoEvent(
    long Seq,
    string Id,
    string BranchId,
    string Type,
    string At,
    string? Actor,
    JsonNode Payload,
    IReadOnlyList<string> Parents,      // 父级依赖（材料/参数/事件/派生结果指纹）
    string InputDigest,                 // 输入摘要
    string? ParameterVersion,
    string? DerivedModelId,
    bool Adopted)                       // 采纳/未采纳都保留
{
    public string StableDisplayId => $"ev-{Seq}-{Id}";
}

/// <summary>项目某分支折叠后的完整可见状态。</summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BranchId { get; set; } = "main";
    public string? BaselineDigest { get; set; }
    public string? ChangedSourceId { get; set; } // 反例分支：相对基线只改动的那一个直接来源
    public long HeadSeq { get; set; }

    public Dictionary<string, DepthDatum> Datums { get; } = new();
    public List<DepthDatum> DatumOrder { get; } = new();
    public Dictionary<string, CoreInterval> Intervals { get; } = new();
    public List<CoreInterval> IntervalOrder { get; } = new();
    public Dictionary<string, GapBoundary> Gaps { get; } = new();
    public List<GapBoundary> GapOrder { get; } = new();
    public Dictionary<string, Anchor> Anchors { get; } = new();
    public List<Anchor> AnchorOrder { get; } = new();
    public Dictionary<string, ReservoirOffset> Reservoirs { get; } = new();
    public List<ReservoirOffset> ReservoirOrder { get; } = new();
    public Dictionary<string, EventLayer> Events { get; } = new();
    public List<EventLayer> EventOrder { get; } = new();
    public Dictionary<string, Exclusion> Exclusions { get; } = new();
    public List<Exclusion> ExclusionOrder { get; } = new();
    public Dictionary<string, ParameterSet> Parameters { get; } = new();
    public List<ParameterSet> ParameterOrder { get; } = new();
    public Dictionary<string, Decision> Decisions { get; } = new();
    public List<Decision> DecisionOrder { get; } = new();
    public Dictionary<string, DerivedModel> Derived { get; } = new();
    public HashSet<string> FrozenModelIds { get; } = new();
    public string CurrentParameterVersion { get; set; } = "v1";
}
