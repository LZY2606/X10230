using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>Severity of an import/validation diagnostic.</summary>
public enum Severity { Error, Warning, Info }

/// <summary>A diagnostic with an explicit position so incomplete input never masquerades as success.</summary>
public sealed record Diagnostic(
    Severity Severity,
    string Code,
    string Message,
    string? EntityId = null,
    string? Field = null,
    int? Line = null,
    string? Snippet = null)
{
    public JsonObject ToNode()
    {
        var n = new JsonObject
        {
            ["severity"] = Severity.ToString().ToLowerInvariant(),
            ["code"] = Code,
            ["message"] = Message,
            ["entityId"] = EntityId,
            ["field"] = Field,
            ["line"] = Line,
            ["snippet"] = Snippet
        };
        return n;
    }
}

/// <summary>The three mandated distinct terminal outcomes.</summary>
public enum OutcomeKind { Success, ResourceBudgetExhausted, CorruptFormat, SemanticallyUnsolvable }

/// <summary>A structured result carrying either a value or positioned diagnostics.</summary>
public sealed class Outcome<T>
{
    public OutcomeKind Kind { get; }
    public T? Value { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Ok => Kind == OutcomeKind.Success;

    private Outcome(OutcomeKind kind, T? value, IReadOnlyList<Diagnostic> diags)
    { Kind = kind; Value = value; Diagnostics = diags; }

    public static Outcome<T> Success(T value, IReadOnlyList<Diagnostic>? diags = null)
        => new(OutcomeKind.Success, value, diags ?? Array.Empty<Diagnostic>());
    public static Outcome<T> ResourceExhausted(IReadOnlyList<Diagnostic> diags)
        => new(OutcomeKind.ResourceBudgetExhausted, default, diags);
    public static Outcome<T> Corrupt(IReadOnlyList<Diagnostic> diags)
        => new(OutcomeKind.CorruptFormat, default, diags);
    public static Outcome<T> Unsolvable(IReadOnlyList<Diagnostic> diags)
        => new(OutcomeKind.SemanticallyUnsolvable, default, diags);
}
