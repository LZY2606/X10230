using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>Depth datum / coordinate basis, e.g. "mbsf" (meters below seafloor).</summary>
public sealed record DepthDatum(string Id, string Name, string Basis);

/// <summary>A continuous cored interval. Gaps between sections are explicit boundaries.</summary>
public sealed record CoreSection(string Id, string CoreId, double TopM, double BottomM, string DatumId);

/// <summary>Explicit stratigraphic boundary: hiatus / unconformity / fault. Age models never interpolate across it.</summary>
public sealed record StratBoundary(string Id, double DepthM, string DatumId, string Kind, string Note);

/// <summary>
/// Radiometric age anchor. The raw representation (e.g. "2450 ± 60 14C yr BP") is immutable;
/// normalization to quantiles happens only in the derived layer. A distribution is never
/// replaced by its mean.
/// </summary>
public sealed record AgeAnchor(
    string Id,
    double DepthM,
    string DatumId,
    string RawAge,          // original representation, preserved verbatim
    string RawUnit,         // e.g. "14C yr BP", "cal yr BP"
    AgeDist? Dist,          // normalized distribution (derived); null when unconvertible
    string? ReservoirOffsetId,
    string MaterialClass);  // e.g. "shell", "wood", "tephra-shard"

/// <summary>Normalized age distribution in cal yr BP, expressed as quantile samples.</summary>
public sealed record AgeDist(
    double P5, double P25, double P50, double P75, double P95,
    string TailKind,        // "symmetric" | "long-tail-old" | "long-tail-young"
    double? Mean);          // kept for diagnostics only; never used by the engine

/// <summary>Marine reservoir offset applied to 14C ages of a material class.</summary>
public sealed record ReservoirOffset(string Id, string Raw, double MeanYr, double SigmaYr, string AppliesTo);

/// <summary>Event layer (e.g. tephra) correlated to an independently dated event.</summary>
public sealed record EventLayer(
    string Id, double DepthM, string DatumId, string Name,
    string CorrelatedEvent, string RawAge, AgeDist? Dist);

/// <summary>An exclusion decision about a material; both adopted and rejected outcomes are kept.</summary>
public sealed record ExclusionRecord(
    string Id, string TargetId, string Reason, string DecidedBy, bool Adopted);

/// <summary>A positioned diagnostic. Incomplete input produces diagnostics, never silent defaults.</summary>
public sealed record Diagnostic(
    string Code,            // e.g. "depth-duplicate", "inverted-segment", "unconvertible-age"
    string Severity,        // "error" | "warning"
    string Position,        // e.g. "anchor:A-04@6.20m" — machine-locatable
    string Message);

public enum ComputeStatus { Ok, NoSolution, BudgetExhausted, FormatCorrupt }

/// <summary>One defensible candidate age-depth model for a segment.</summary>
public sealed record CandidateModel(
    string Id,
    string SegmentId,
    IReadOnlyList<AnchorRef> UsedAnchors,
    IReadOnlyList<AnchorRef> DroppedAnchors,
    IReadOnlyList<ModelPoint> Points,       // monotonic in depth and in age
    IReadOnlyList<RateInterval> Rates,
    string Rationale);

public sealed record AnchorRef(string AnchorId, double DepthM);
public sealed record ModelPoint(double DepthM, double AgeP5, double AgeP50, double AgeP95);
public sealed record RateInterval(double TopM, double BottomM, double RateMinYrPerM, double RateMaxYrPerM);

/// <summary>Derived result for one continuous segment, keyed by input digest for local recompute.</summary>
public sealed record SegmentResult(
    string SegmentKey,
    string InputDigest,
    string ParamVersion,
    ComputeStatus Status,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A frozen model set: snapshot of adopted candidates at a model-set version.</summary>
public sealed record FrozenModel(
    string FreezeId, long ModelSetVersion, string FrozenBy,
    IReadOnlyDictionary<string, string> AdoptedCandidateBySegment,
    IReadOnlyList<ModelPoint> Points,
    string AtEventId);

/// <summary>Impact of a new anchor relative to a frozen model.</summary>
public sealed record ImpactAssessment(
    string NewAnchorId, string FreezeId, string SegmentKey,
    double? FrozenMedianAtDepth, double? NewMedianAtDepth,
    double? BandWidthBefore, double? BandWidthAfter,
    bool WithinFrozenBand, string Verdict);
