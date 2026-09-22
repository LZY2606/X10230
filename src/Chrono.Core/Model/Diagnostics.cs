namespace Chrono.Core.Model;

/// <summary>Severity of a structured diagnostic. Only <see cref="Error"/> makes the result unsolvable.</summary>
public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// A structured, machine-readable diagnostic. <paramref name="Where"/> always carries a stable
/// material/field location so incomplete input can never be silently replaced by a default.
/// </summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Where,
    string Message,
    IReadOnlyDictionary<string, string>? Context = null);

/// <summary>Three deliberately distinct failure families required by the specification.</summary>
public enum FailureKind
{
    /// <summary>Input text/package cannot be parsed at all.</summary>
    FormatCorrupt,
    /// <summary>Input parses but the stated resource budget is insufficient.</summary>
    BudgetExhausted,
    /// <summary>Input is complete and well formed, but no legal model exists yet (needs a human decision).</summary>
    SemanticUnresolvable,
    /// <summary>A frozen branch was asked to mutate.</summary>
    ConflictFrozen,
}

/// <summary>Outcome of an import/derive operation: success, or one of the distinct failure families.</summary>
public sealed record Outcome<T>(
    bool Ok,
    T? Value,
    FailureKind? Failure,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static Outcome<T> Success(T value, IReadOnlyList<Diagnostic>? diagnostics = null)
        => new(true, value, null, diagnostics ?? Array.Empty<Diagnostic>());

    public static Outcome<T> Fail(FailureKind kind, IReadOnlyList<Diagnostic> diagnostics)
        => new(false, default, kind, diagnostics);

    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error);
}
