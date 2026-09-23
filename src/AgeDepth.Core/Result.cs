namespace AgeDepth.Core;

/// <summary>
/// Distinct machine-readable outcome kinds. BudgetExhausted, FormatCorrupt and
/// SemanticallyUnsolvable are deliberately separate results, never collapsed.
/// </summary>
public enum ResultKind
{
    Ok,
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnsolvable,
    Conflict,
    NotFound
}

public sealed record Diagnostic(string Code, string Message, string Location, string Severity = "error");

public class Result
{
    public ResultKind Kind { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsOk => Kind == ResultKind.Ok;

    protected Result(ResultKind kind, IReadOnlyList<Diagnostic> diagnostics)
    {
        Kind = kind;
        Diagnostics = diagnostics;
    }

    public static Result Success(IReadOnlyList<Diagnostic>? diagnostics = null)
        => new(ResultKind.Ok, diagnostics ?? Array.Empty<Diagnostic>());

    public static Result Failure(ResultKind kind, params Diagnostic[] diagnostics)
        => new(kind, diagnostics);

    public static Result<T> Success<T>(T value, IReadOnlyList<Diagnostic>? diagnostics = null)
        => new(value, ResultKind.Ok, diagnostics ?? Array.Empty<Diagnostic>());

    public static Result<T> Failure<T>(ResultKind kind, params Diagnostic[] diagnostics)
        => new(default, kind, diagnostics);
}

public sealed class Result<T> : Result
{
    public T? Value { get; }

    internal Result(T? value, ResultKind kind, IReadOnlyList<Diagnostic> diagnostics) : base(kind, diagnostics)
    {
        Value = value;
    }
}
