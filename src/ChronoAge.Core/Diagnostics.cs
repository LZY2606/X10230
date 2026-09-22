using System.Text.Json.Serialization;

namespace ChronoAge.Core;

/// <summary>Severity of a diagnostic. Errors stop success; warnings never fake it.</summary>
public enum Severity { Info, Warning, Error }

/// <summary>
/// A diagnostic carrying an explicit position (object id, field path, source fragment)
/// instead of silently substituting nulls or defaults.
/// </summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? ObjectId = null,
    string? Field = null,
    string? SourceFragment = null)
{
    public bool IsError => Severity == Severity.Error;
}

/// <summary>The three structurally different failure outcomes the spec requires.</summary>
public enum OutcomeKind { Ok, Incomplete, FormatCorrupt, BudgetExhausted, SemanticallyUnsolvable, Conflict }

/// <summary>Result envelope: either a value or positioned diagnostics, never a silent null.</summary>
public sealed class Outcome<T>
{
    public OutcomeKind Kind { get; }
    public T? Value { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsOk => Kind == OutcomeKind.Ok;

    private Outcome(OutcomeKind kind, T? value, IReadOnlyList<Diagnostic> diagnostics)
    {
        Kind = kind; Value = value; Diagnostics = diagnostics;
    }

    public static Outcome<T> Success(T value) => new(OutcomeKind.Ok, value, Array.Empty<Diagnostic>());
    public static Outcome<T> Incomplete(IEnumerable<Diagnostic> d) => new(OutcomeKind.Incomplete, default, d.ToList());
    public static Outcome<T> FormatCorrupt(IEnumerable<Diagnostic> d) => new(OutcomeKind.FormatCorrupt, default, d.ToList());
    public static Outcome<T> Budget(IEnumerable<Diagnostic> d) => new(OutcomeKind.BudgetExhausted, default, d.ToList());
    public static Outcome<T> Unsolvable(IEnumerable<Diagnostic> d) => new(OutcomeKind.SemanticallyUnsolvable, default, d.ToList());
    public static Outcome<T> Conflict(IEnumerable<Diagnostic> d) => new(OutcomeKind.Conflict, default, d.ToList());
}

public static class Diag
{
    public static Diagnostic Error(string code, string msg, string? id = null, string? field = null, string? frag = null)
        => new(code, Severity.Error, msg, id, field, frag);
    public static Diagnostic Warn(string code, string msg, string? id = null, string? field = null, string? frag = null)
        => new(code, Severity.Warning, msg, id, field, frag);
    public static Diagnostic Info(string code, string msg, string? id = null, string? field = null, string? frag = null)
        => new(code, Severity.Info, msg, id, field, frag);
}
