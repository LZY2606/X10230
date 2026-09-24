namespace Chrono.Core.Domain;

public enum Severity { Info, Warning, Error }

/// <summary>A positioned diagnostic. Incomplete input never silently becomes
/// null/default; the location always points back at the source fragment.</summary>
public sealed record Diagnostic(string Code, string Message, string Location, Severity Severity = Severity.Error)
{
    public override string ToString() => $"[{Code}] {Location}: {Message}";
}

/// <summary>Three genuinely different failure classes, plus success.</summary>
public enum OutcomeKind { Ok, FormatCorrupt, SemanticImpossible, ResourceBudgetExhausted }

public sealed record Outcome<T>(OutcomeKind Kind, T? Value, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Kind == OutcomeKind.Ok;
    public static Outcome<T> Ok(T value) => new(OutcomeKind.Ok, value, Array.Empty<Diagnostic>());
    public static Outcome<T> Format(IReadOnlyList<Diagnostic> d) => new(OutcomeKind.FormatCorrupt, default, d);
    public static Outcome<T> Impossible(IReadOnlyList<Diagnostic> d) => new(OutcomeKind.SemanticImpossible, default, d);
    public static Outcome<T> Budget(IReadOnlyList<Diagnostic> d) => new(OutcomeKind.ResourceBudgetExhausted, default, d);
}
