namespace ChronoCore.Domain.Model;

public enum EventKind
{
    PackageImported = 0,
    RulesetChosen = 1,
    DerivationRun = 2,
    CandidateAccepted = 3,
    CandidateRejected = 4,
    CandidateFrozen = 5,
    ParameterAdjusted = 6,
    AnchorProposed = 7,
    BranchCreated = 8,
    CounterfactualBaseline = 9,
    SourceAmended = 10
}

/// <summary>Append-only provenance event. Immutable material is referenced;
/// decisions are themselves events. Each event names its parents, the ruleset
/// version in effect and an input digest, so replay is deterministic and any
/// historical state can be reconstructed.</summary>
public sealed record ChronoEvent(
    string EventId,
    EventKind Kind,
    string BranchId,
    string ParentEventId,
    string MaterialPackageId,
    string RulesetVersion,
    string InputDigest,
    string PayloadJson,
    string Actor,
    DateTimeOffset CreatedAt,
    long Sequence,
    string? FrozenCandidateId);
