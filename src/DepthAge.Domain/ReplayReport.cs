namespace DepthAge.Domain;

public sealed record AssertionResult
{
    public required string Token { get; init; }
    public required bool Passed { get; init; }
    public required string Detail { get; init; }
}

public sealed record ReplayStepResult
{
    public required string StepId { get; init; }
    public required string Label { get; init; }
    public required string Branch { get; init; }
    public required bool Passed { get; init; }
    public required string[] EventIds { get; init; }
    public required string[] DiagnosticCodes { get; init; }
    public string? DerivationId { get; init; }
    public string? Status { get; init; }
    public required string[] AffectedObjects { get; init; }
    public string? Error { get; init; }
    public required AssertionResult[] Assertions { get; init; }
}

/// <summary>
/// Machine-readable replay evidence: every step's inputs (events), judgements (diagnostics),
/// affected objects and assertion results. BaselineDigest pins the "all material retained"
/// starting state so single-source changes can be diffed against it.
/// </summary>
public sealed record ReplayReport
{
    public required string CasePackId { get; init; }
    public required string ReplayedAt { get; init; }
    public required bool Passed { get; init; }
    public string? BaselineDigest { get; init; }
    public required ReplayStepResult[] Steps { get; init; }
    public required AssertionResult[] GlobalAssertions { get; init; }
}
