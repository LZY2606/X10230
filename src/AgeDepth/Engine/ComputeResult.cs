namespace AgeDepth.Engine;

/// <summary>The three distinct terminal outcomes are never collapsed into one.</summary>
public enum ComputeStatus
{
    Ok,
    /// <summary>Rejection sampling exhausted the iteration budget without the full ensemble.</summary>
    BudgetExhausted,
    /// <summary>Input bytes/values are structurally unusable.</summary>
    CorruptInput,
    /// <summary>Input is well formed but the geological constraints have no solution.</summary>
    Unsolvable
}

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string Position { get; init; }
    public string? ObjectId { get; init; }
}
