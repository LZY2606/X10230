namespace AgeDepth.Domain;

/// <summary>带位置的诊断。输入不完整时返回诊断，不得用空值或默认值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,        // 机器可读码，例如 DEPTH_DUPLICATE / UNIT_UNKNOWN / ANCHOR_MEAN_ONLY
    string Severity,    // "error" | "warning"
    string Position,    // 位置：材料 id、JSON 路径或深度
    string Message);

/// <summary>三种必须区分的失败结果 + 成功。</summary>
public abstract record Outcome
{
    public sealed record Ok : Outcome;
    /// <summary>资源预算耗尽（候选数/抽样数超限）。</summary>
    public sealed record BudgetExhausted(string Scope, int Limit, int Requested) : Outcome;
    /// <summary>格式损坏（JSON 截断、半写文件、校验和不匹配）。</summary>
    public sealed record FormatCorrupt(string Position, string Detail) : Outcome;
    /// <summary>语义无解（约束冲突无法构造任何合法候选）。</summary>
    public sealed record SemanticUnsolvable(IReadOnlyList<Diagnostic> Reasons) : Outcome;
}

public sealed record Result<T>(T? Value, Outcome Outcome, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsOk => Outcome is Outcome.Ok;
    public static Result<T> Success(T value, IReadOnlyList<Diagnostic>? diags = null)
        => new(value, new Outcome.Ok(), diags ?? Array.Empty<Diagnostic>());
    public static Result<T> Fail(Outcome outcome, IReadOnlyList<Diagnostic>? diags = null)
        => new(default, outcome, diags ?? Array.Empty<Diagnostic>());
}
