using System;

namespace ChronoCore.Domain.Common;

/// <summary>
/// The three contractually distinct failure results. They must never collapse into one:
/// <list type="bullet">
/// <item><see cref="Malformed"/> - the bytes/structure are corrupt and never entered the store;</item>
/// <item><see cref="BudgetExhausted"/> - structurally valid, semantically legal, but resource cap reached;</item>
/// <item><see cref="SemanticNoSolution"/> - structurally valid but no defensible model satisfies the rules.</item>
/// </list>
/// </summary>
public enum DomainOutcomeKind
{
    Ok = 0,
    Malformed = 1,
    BudgetExhausted = 2,
    SemanticNoSolution = 3
}

public sealed record Diagnostic(
    string Code,
    string Message,
    string? Path = null,
    string? ObjectId = null,
    string? RawValue = null)
{
    public string Location => Path is null ? (ObjectId ?? "?") : ObjectId is null ? Path : $"{Path}#{ObjectId}";
}

public sealed class DomainException : Exception
{
    public Diagnostic Diagnostic { get; }

    public DomainException(Diagnostic diagnostic) : base(diagnostic.Message)
    {
        Diagnostic = diagnostic;
    }
}
