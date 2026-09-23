namespace ChronoCore.Domain;

public enum Severity { Error, Warning, Info }

/// <summary>输入/语义诊断，必须带位置；输入不完整绝不静默成功。</summary>
public sealed record Diagnostic(
    Severity Severity,
    string Code,
    string Message,
    string? Path = null,
    int? Line = null,
    int? Column = null,
    string? Snippet = null)
{
    public string Position =>
        Path is null ? (Line is null ? "?" : $"{Line}:{Column ?? 0}") : $"{Path}#{Line?.ToString() ?? "?"}";
}

/// <summary>三种必须可区分的终态：资源预算耗尽 / 格式损坏 / 语义无解。</summary>
public enum FailureKind
{
    BudgetExhausted,   // 计算本身正确，但候选/样本预算耗尽
    FormatCorrupt,     // 字节/结构无法解析，无法转换值留在原位
    SemanticUnsolvable // 结构合法，但不存在任何合法模型
}

public sealed class Failure : Exception
{
    public FailureKind Kind { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public Failure(FailureKind kind, string message, IReadOnlyList<Diagnostic>? diagnostics = null)
        : base(message)
    {
        Kind = kind;
        Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
    }
}

/// <summary>带诊断的导入结果。失败不抛空值/默认值伪装成功。</summary>
public sealed record ImportReport(
    bool Ok,
    FailureKind? Failure,
    IReadOnlyList<Diagnostic> Diagnostics,
    Materials? Materials,
    int? AcceptedFrames,
    int? TornFrames)
{
    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error);
}
