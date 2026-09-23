using System.Text.Json.Nodes;
namespace AgeDepth.Core;

/// <summary>推导结果：预算耗尽、格式损坏、语义无解是三种不同结果。</summary>
public abstract record DeriveResult
{
    public sealed record Ok(DerivedState State) : DeriveResult;
    public sealed record BudgetExhausted(int CombinationsNeeded, int Budget) : DeriveResult;
    public sealed record Unsolvable(IReadOnlyList<Diagnostic> Diagnostics) : DeriveResult;
}

public abstract record ImportResult
{
    public sealed record Ok(int Materials, int Decisions, int Events) : ImportResult;
    public sealed record CorruptFormat(string Message, string Path) : ImportResult;
}

public abstract record FreezeResult
{
    public sealed record Ok(Candidate Frozen) : FreezeResult;
    public sealed record Conflict(string CandidateId, int ExpectedVersion) : FreezeResult;
    public sealed record NotFound(string CandidateId) : FreezeResult;
}

public sealed record DerivedState(
    Segment[] Segments,
    Candidate[] Candidates,
    Diagnostic[] Diagnostics,
    int ParamVersion)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["segments"] = new JsonArray(Segments.Select(s => s.ToJson()).ToArray()),
        ["candidates"] = new JsonArray(Candidates.Select(c => c.ToJson()).ToArray()),
        ["diagnostics"] = new JsonArray(Diagnostics.Select(d => d.ToJson()).ToArray()),
        ["paramVersion"] = ParamVersion,
    };
}
