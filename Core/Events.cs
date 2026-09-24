namespace DepthAge.Core;

/// <summary>
/// 追加式事件。每个计算结果都记录输入摘要（InputDigest）、参数版本（ParamVersion）
/// 与父级依赖（Parents：材料身份/目标事件）。事件只能追加，回放到任意旧事件即恢复当时结论。
/// </summary>
public sealed record ModelEvent(
    long Seq,
    string Id,
    string Kind,
    string PayloadJson,
    string InputDigest,
    string ParamVersion,
    IReadOnlyList<string> Parents,
    string Created)
{
    public const string ImportKind = "import";
    public const string ImportMaterialKind = "import-material";
    public const string FitKind = "fit";
    public const string ParamRevisionKind = "param-revision";
    public const string SourceChangeKind = "source-change";
    public const string DecisionKind = "decision";
    public const string FreezeKind = "freeze";
    public const string FreezeConflictKind = "freeze-conflict";
    public const string RecoveryKind = "recovery";
}

public sealed record DecisionPayload(string Decision, string Target, string Reason, string By, string Branch)
{
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Adjust = "adjust";
    public const string MainBranch = "main";
}

public sealed record ParamRevisionPayload(
    string ParamVersion,
    double DefaultReservoirOffsetKa,
    string Reason,
    string By);

public sealed record FreezePayload(
    string ModelDigest,
    string Branch,
    string By,
    ModelSet Snapshot);

public sealed record FreezeRow(string ModelId, string Branch, string By, long Seq, string Created);
