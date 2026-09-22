using CoreChrono.Domain;
using CoreChrono.Engine;

namespace CoreChrono.CasePack;

/// <summary>A replayable step in the domain narrative. Each step declares the
/// mutation it applies and the structured situation the reviewer must be able to
/// trace back to a specific material / rule version / decision.</summary>
public sealed record CaseStep
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Narrative { get; init; }
    public required string Action { get; init; }   // import|derive|add-anchor|change-source|revise-params|decision|freeze|compare|expect
    public Dictionary<string, object>? Args { get; init; }
    public List<string> ExpectJudgmentCodes { get; init; } = new();
    public string? ExpectStatus { get; init; }
}

public sealed record CasePackBundle(
    string Id, string Title, string BaselineJson, List<CaseStep> Steps, string Narrative);
