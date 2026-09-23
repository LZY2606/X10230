namespace DepthAgeReview.Domain;

public enum DiagnosticSeverity { Error, Warning, Info }

/// <summary>带位置的诊断。Position 指向输入片段（如 fragment:anchors.json#line=12）。</summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    string? Position = null,
    string? ObjectId = null)
{
    public override string ToString() => $"[{Code}] {Message}" + (Position is null ? "" : $" @{Position}") + (ObjectId is null ? "" : $" (对象 {ObjectId})");
}

/// <summary>
/// 三种必须区分的顶层结果。<see cref="BuildOutcomeKind"/>：
/// Ok=成功（可能附带诊断）；FormatCorrupt=格式损坏（输入无法解析）；
/// SemanticUnresolvable=语义无解（可解析但无合法模型，如不断层段中倒置且不重叠）；
/// BudgetExhausted=资源预算耗尽（候选生成被预算截断，结果不完整）。
/// 它们绝不允许互相伪装。
/// </summary>
public enum BuildOutcomeKind { Ok, FormatCorrupt, SemanticUnresolvable, BudgetExhausted }
