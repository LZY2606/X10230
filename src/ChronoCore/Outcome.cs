namespace ChronoCore;

/// <summary>
/// The three structurally different terminal outcomes required by the domain:
/// resource budget exhaustion, format corruption and semantic impossibility
/// must never collapse into one generic failure.
/// </summary>
public enum DerivationStatus
{
    Ok = 0,
    BudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticUnsolvable = 3,
    IncompleteInput = 4
}

public enum DiagnosticSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2
}

/// <summary>A located diagnostic. Position always points at an import fragment
/// or a material id; diagnostics are never silently dropped.</summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string Location,
    string? Value = null)
{
    public override string ToString() => $"[{Severity}] {Code} @ {Location}: {Message}";
}

public sealed record DerivationResult(
    DerivationStatus Status,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Diagnostic> Diagnostics,
    string InputDigest,
    string ParamVersion,
    int DrawCount,
    int DrawBudget,
    IReadOnlyList<string> ParentDerivationIds)
{
    public bool Succeeded => Status == DerivationStatus.Ok;

    public static DerivationResult Fail(DerivationStatus status, string code, string message,
        string location, string inputDigest, string paramVersion,
        IReadOnlyList<string>? parents = null, int draws = 0, int budget = 0) =>
        new(status, Array.Empty<CandidateModel>(), Array.Empty<Finding>(),
            new[] { new Diagnostic(DiagnosticSeverity.Error, code, message, location) },
            inputDigest, paramVersion, draws, budget, parents ?? Array.Empty<string>());
}
