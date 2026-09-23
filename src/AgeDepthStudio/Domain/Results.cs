namespace AgeDepthStudio.Domain;

/// <summary>
/// Distinct failure outcomes. Budget exhaustion, corrupt formats and
/// semantically unsolvable inputs must never collapse into one another.
/// </summary>
public enum FailureKind
{
    None = 0,
    FormatCorrupt = 1,
    BudgetExhausted = 2,
    SemanticUnsolvable = 3,
    Conflict = 4
}

public sealed record Diagnostic(string Code, string Path, string Message, string Severity = "error");

public sealed class OpResult<T>
{
    public T? Value { get; }
    public FailureKind Failure { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Ok => Failure == FailureKind.None;

    private OpResult(T? value, FailureKind failure, IReadOnlyList<Diagnostic> diagnostics)
    {
        Value = value;
        Failure = failure;
        Diagnostics = diagnostics;
    }

    public static OpResult<T> Success(T value, IReadOnlyList<Diagnostic>? diagnostics = null)
        => new(value, FailureKind.None, diagnostics ?? Array.Empty<Diagnostic>());

    public static OpResult<T> Fail(FailureKind kind, IReadOnlyList<Diagnostic> diagnostics)
        => new(default, kind, diagnostics);

    public static OpResult<T> Fail(FailureKind kind, params Diagnostic[] diagnostics)
        => new(default, kind, diagnostics);
}
