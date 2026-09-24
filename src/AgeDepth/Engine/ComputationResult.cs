using AgeDepth.Domain;

namespace AgeDepth.Engine;

/// <summary>
/// Budget exhaustion, corrupt format and semantic unsolvability are three
/// distinct results, never collapsed into a generic failure.
/// </summary>
public abstract record ComputationResult<T>
{
    public sealed record Ok(T Value) : ComputationResult<T>;
    public sealed record BudgetExhausted(string Scope, int Used, int Limit) : ComputationResult<T>;
    public sealed record CorruptFormat(string Position, string Detail) : ComputationResult<T>;
    public sealed record Unsolvable(string Reason, IReadOnlyList<Diagnostic> Diagnostics) : ComputationResult<T>;
}
