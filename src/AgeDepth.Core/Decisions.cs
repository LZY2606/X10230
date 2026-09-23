namespace AgeDepth.Core;

/// <summary>
/// Mutable decision layer. Decisions never edit materials; they are appended
/// as events and can be revised by later events, keeping the full history.
/// </summary>
public sealed record ModelParameters(
    string Version,
    double MonotonicToleranceYr,
    int MaxCandidates,
    int SamplesPerAnchor,
    int Seed)
{
    public static ModelParameters Default => new(
        Version: "rules/v1",
        MonotonicToleranceYr: 0.0,
        MaxCandidates: 64,
        SamplesPerAnchor: 200,
        Seed: 20260919);
}

public enum AnchorVerdict { Accepted, Rejected }

/// <summary>A human decision about an anchor; conflicts are kept, not merged.</summary>
public sealed record AnchorDecision(
    string Id,
    string AnchorId,
    AnchorVerdict Verdict,
    string Reason,
    string DecidedBy,
    string DecidedAt,
    string? Supersedes);

/// <summary>A freeze pins a model version; concurrent freezes conflict by CAS.</summary>
public sealed record FreezeRecord(
    string ModelVersionId,
    long EventSeq,
    string FrozenAt);

public enum FreezeStatus { Frozen, Conflict }

public sealed record FreezeResult(FreezeStatus Status, string ModelVersionId, string? Message);
