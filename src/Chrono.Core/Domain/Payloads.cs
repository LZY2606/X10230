using Chrono.Core.Models;

namespace Chrono.Core.Domain;

public sealed record ProjectCreatedPayload(string ProjectId, string Name, ParameterSet Parameters);
public sealed record ImportBatchPayload(
    DepthDatum? Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments);
public sealed record ParameterRevisionPayload(ParameterSet Parameters, string Reason, IReadOnlyList<string> AffectedAnchorIds);
public sealed record SourceRevisionPayload(string AnchorId, AgeAnchor RevisedAnchor, IReadOnlyList<string> LocalDependencyIds, string Reason);
public sealed record AnchorExcludedPayload(string AnchorId, string ExclusionId, string Reason);
public sealed record AnchorRestoredPayload(string AnchorId, string ExclusionId, string Reason);
public sealed record DecisionConflictPayload(string DecisionId, string OptionAText, string OptionBText,
    string ChosenBranchId, string Note, IReadOnlyList<string> AffectedIds);
public sealed record BranchCreatedPayload(string BranchId, string Name, string? ParentBranchId, bool IsScratch, string Note);
public sealed record ModelDerivedPayload(ModelCandidate Candidate);
public sealed record ModelFrozenPayload(string CandidateId);
public sealed record JudgmentPayload(Judgment Judgment);
public sealed record ProbePayload(ProbeRecord Probe);
public sealed record GenericIdPayload(string Id, string Reason);
