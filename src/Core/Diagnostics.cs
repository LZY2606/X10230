namespace AgeModel.Core;

/// <summary>Structured diagnostic with a stable code and a machine-readable position.</summary>
public sealed record Diagnostic(
    string Code,
    string Severity, // "error" | "warning" | "info"
    string Message,
    Position? Position = null,
    string? MaterialId = null,
    string? MaterialVersionId = null)
{
    public static Diagnostic Error(string code, string message, Position? pos = null, string? materialId = null, string? versionId = null)
        => new(code, "error", message, pos, materialId, versionId);
    public static Diagnostic Warning(string code, string message, Position? pos = null, string? materialId = null, string? versionId = null)
        => new(code, "warning", message, pos, materialId, versionId);
    public static Diagnostic Info(string code, string message, Position? pos = null, string? materialId = null, string? versionId = null)
        => new(code, "info", message, pos, materialId, versionId);
}

/// <summary>Machine-readable position of a diagnostic: material, JSON pointer and human line.</summary>
public sealed record Position(string? MaterialId, string? Pointer, int? Line = null, int? Column = null)
{
    public override string ToString() =>
        $"{MaterialId ?? "-"}{(Pointer is null ? "" : " " + Pointer)}{(Line is null ? "" : $" L{Line}:{Column ?? 1}")}";
}

/// <summary>
/// Outcome of an engine operation. The three failure modes are distinct results, never
/// conflated: budget exhaustion, format corruption and semantic unsolvability.
/// </summary>
public abstract record EngineOutcome
{
    public sealed record Ok<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics) : EngineOutcome;
    public sealed record BudgetExhausted(string Scope, long Spent, long Limit, IReadOnlyList<Diagnostic> Diagnostics) : EngineOutcome;
    public sealed record FormatCorrupt(IReadOnlyList<Diagnostic> Diagnostics) : EngineOutcome;
    public sealed record SemanticImpossible(string Reason, IReadOnlyList<Diagnostic> Diagnostics) : EngineOutcome;

    public bool IsOk => this is Ok<object>;
}

public static class Outcome
{
    public static EngineOutcome.Ok<T> Ok<T>(T value, params Diagnostic[] diags) => new(value, diags);
    public static EngineOutcome Ok<T>(T value, IReadOnlyList<Diagnostic> diags) => new EngineOutcome.Ok<T>(value, diags);
    public static EngineOutcome Budget(string scope, long spent, long limit, params Diagnostic[] diags)
        => new EngineOutcome.BudgetExhausted(scope, spent, limit, diags);
    public static EngineOutcome Corrupt(params Diagnostic[] diags) => new EngineOutcome.FormatCorrupt(diags);
    public static EngineOutcome Impossible(string reason, params Diagnostic[] diags)
        => new EngineOutcome.SemanticImpossible(reason, diags);
}
