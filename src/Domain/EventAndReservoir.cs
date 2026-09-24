using ChronoCore.Quantities;

namespace ChronoCore.Domain;

/// <summary>Reservoir / radiocarbon offset material (ΔR etc.).</summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required Measure Delta { get; init; }
    public Measure? Sigma { get; init; }
    public required string RawEvidence { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
}

/// <summary>An event layer (tephra, turbidite, ...) that anchors can be correlated to.</summary>
public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string SectionId { get; init; }
    public required Measure Depth { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; } // tephra, flood, turbidite ...
    /// <summary>External correlation reference, e.g. known tephra isochron with its own age basis.</summary>
    public string? CorrelationRef { get; init; }
    public Measure? CorrelatedAge { get; init; }
    public required string RawEvidence { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
}

/// <summary>
/// An exclusion decision is itself retained material: what was excluded, by which
/// rule version, and why. Nothing is silently dropped.
/// </summary>
public sealed record Exclusion
{
    public required string Id { get; init; }
    public required string TargetKind { get; init; } // anchor, event, section ...
    public required string TargetId { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
    public required string RuleVersion { get; init; }
    public string? DecidedBy { get; init; } // material-source vs human decision id
}
