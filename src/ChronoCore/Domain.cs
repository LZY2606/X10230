using System.Text.Json.Serialization;

namespace ChronoCore;

// ---------------------------------------------------------------------------
// Immutable source materials ("不可变材料" layer).
// Every domain quantity keeps its raw representation plus its normalization.
// Values that cannot be converted stay on the material with a diagnostic and
// are never promoted into a derivation or dropped for plotting.
// ---------------------------------------------------------------------------

public enum AnchorKind { Radiocarbon = 0, EventBed = 1, BioEvent = 2, Calendar = 3 }

public enum AnchorState { Active = 0, Excluded = 1 }

public enum DistributionKind { Normal = 0, EmpiricalQuantiles = 1 }

public enum BreakKind { CoreGap = 0, Unconformity = 1 }

/// <summary>A measured depth with its original text, unit and datum binding.</summary>
public sealed record DepthMeasure(
    string Raw,
    string RawUnit,
    string DatumId,
    double? ValueCm,
    string? UnconvertibleCode,
    string? UnconvertibleReason)
{
    public bool IsResolved => ValueCm is not null && UnconvertibleCode is null;
}

public sealed record Datum(
    string Id,
    string Name,
    string ZeroReference,
    string PositiveDirection,
    string DepthUnit);

/// <summary>A core gap or an unconformity. Both are explicit age-model
/// boundaries: monotonicity is never enforced across them.</summary>
public sealed record SegmentBreak(
    string Id,
    double TopDepthCm,
    double BottomDepthCm,
    BreakKind Kind,
    string Label,
    string SourceFragment);

public sealed record LithoSegment(
    string Id,
    string Name,
    double TopDepthCm,
    double BaseDepthCm,
    string Lithology);

/// <summary>Calendar-age probability information attached to an anchor.
/// A distribution is always carried as a distribution; it is legal to read a
/// mean off it, but never legal to replace the distribution with the mean.</summary>
public sealed record AgeDistribution(
    DistributionKind Kind,
    double? MeanCalBp,
    double? SigmaCalBp,
    IReadOnlyList<QuantilePoint>? Quantiles,
    bool LongTail,
    string Basis,
    string? CalibrationCurveId)
{
    public IReadOnlyList<QuantilePoint> EffectiveQuantiles =>
        Quantiles ?? Array.Empty<QuantilePoint>();
}

public sealed record QuantilePoint(double Q, double AgeCalBp);

/// <summary>Radiocarbon raw evidence. Calibration (with reservoir correction)
/// happens in the derivation layer, never by mutating the material.</summary>
public sealed record RadiocarbonEvidence(
    double Age14CBp,
    double Sigma14CYears,
    double ReservoirOffsetYears,
    double ReservoirSigmaYears,
    string CalibrationCurveId);

public sealed record Anchor
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required AnchorKind Kind { get; init; }
    public required DepthMeasure Depth { get; init; }
    public AnchorState State { get; init; } = AnchorState.Active;
    public string? EventBedId { get; init; }
    public string? ExclusionId { get; init; }
    public RadiocarbonEvidence? Radiocarbon { get; init; }
    /// <summary>Calendar distribution supplied directly (tephra / bio-event /
    /// direct date). Null for uncalibrated radiocarbon.</summary>
    public AgeDistribution? SuppliedCalendar { get; init; }
    public string SourceFragment { get; init; } = "";
    public string Provenance { get; init; } = "";
}

/// <summary>An event bed (e.g. tephra) with its cross-core correlation.</summary>
public sealed record EventBed
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required DepthMeasure Depth { get; init; }
    public required string MarkerId { get; init; }
    public required string CorrelationBasis { get; init; }
    public required AgeDistribution CorrelatedAge { get; init; }
    public AnchorState State { get; init; } = AnchorState.Active;
    public string? ExclusionId { get; init; }
    public string SourceFragment { get; init; } = "";
}

/// <summary>An exclusion decision is itself retained forever: what was
/// excluded, why, by which rule version and by whom.</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetKind { get; init; }   // "anchor" | "event-bed"
    public required string TargetId { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string DecisionEventId { get; init; }
    public bool Revoked { get; init; }
    public string? RevokeEventId { get; init; }
}

/// <summary>An observation whose value could not be converted/bound. It stays
/// at its original position with a diagnostic instead of becoming a null.</summary>
public sealed record UnresolvedObservation
{
    public required string Id { get; init; }
    public required string Kind { get; init; }        // "depth" | "age"
    public required string RawText { get; init; }
    public required string RawUnit { get; init; }
    public required string IntendedTarget { get; init; }
    public required string Location { get; init; }
    public required string DiagnosticCode { get; init; }
    public required string DiagnosticMessage { get; init; }
}

/// <summary>Tunable derivation parameters. Revisions create a new version;
/// history always keeps the version that produced each result.</summary>
public sealed record ParamSet(
    string Version,
    string CalibrationCurveId,
    double MinRateCmPerKa,
    double MaxRateCmPerKa,
    double MinMonotoneDrawFraction,
    int DrawBudget,
    int MaxCandidates,
    string? ParentVersion,
    string Note)
{
    public const string RuleVersion = "rules-2026.1";
}

/// <summary>The full immutable material bundle captured at import (and on
/// later material-changing events a new snapshot is produced).</summary>
public sealed record Materials(
    string BundleId,
    Datum Datum,
    IReadOnlyList<LithoSegment> LithoSegments,
    IReadOnlyList<SegmentBreak> Breaks,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<EventBed> EventBeds,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<UnresolvedObservation> Unresolved,
    ParamSet Params,
    string SourceLabel);

// ---------------------------------------------------------------------------
// Derived layer
// ---------------------------------------------------------------------------

public enum FindingSeverity { Info = 0, Warning = 1, Violation = 2 }

public sealed record Finding(
    string Code,
    FindingSeverity Severity,
    string Message,
    IReadOnlyList<string> AffectedIds,
    string? SegmentId = null);

public sealed record RateInterval
{
    public required string Id { get; init; }             // stable display identity
    public required string SegmentId { get; init; }
    public required string FromAnchorId { get; init; }
    public required string ToAnchorId { get; init; }
    public required double MedianCmPerKa { get; init; }
    public required double LowCmPerKa { get; init; }      // q=0.05
    public required double HighCmPerKa { get; init; }     // q=0.95
    public required double MonotoneDrawFraction { get; init; }
    public required double DistanceCm { get; init; }
}

public sealed record BandPoint
{
    public required string Id { get; init; }             // stable display identity
    public required string SegmentId { get; init; }
    public required double DepthCm { get; init; }
    public required double MedianCalBp { get; init; }
    public required double LowCalBp { get; init; }
    public required double HighCalBp { get; init; }
}

public sealed record SegmentAgeSummary(
    string SegmentId,
    double TopDepthCm,
    double BottomDepthCm,
    bool Constrained,
    int AnchorCount,
    double MonotoneDrawFraction,
    IReadOnlyList<string> AnchorIds);

public sealed record CandidateModel
{
    public required string Id { get; init; }
    public required string RankKey { get; init; }        // deterministic tie ordering
    public required int Rank { get; init; }
    public required double Score { get; init; }
    public required bool Tie { get; init; }
    public required string? TieGroupId { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required IReadOnlyList<BandPoint> Band { get; init; }
    public required IReadOnlyList<SegmentAgeSummary> Segments { get; init; }
    public required double OverallMonotoneFraction { get; init; }
    /// <summary>True when the engine could not prove the model admissible and
    /// it exists only as a rejected counterexample.</summary>
    public required bool Rejected { get; init; }
    public string? RejectReason { get; init; }
}

/// <summary>Machine-readable statement of one domain judgement. These power
/// the counterexample archive and are also replayed by the test suite.</summary>
public sealed record RuleJudgement(
    string Code,
    bool Holds,
    string Statement,
    IReadOnlyList<string> EvidenceEventIds,
    IReadOnlyList<string> AffectedIds,
    string RuleVersion);
