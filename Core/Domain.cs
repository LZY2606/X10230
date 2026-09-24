using System.Text.Json.Serialization;

namespace AgeDepth.Core;

public enum BoundaryKind { Gap, Unconformity, Hiatus }
public enum DatingKind { RadiocarbonCalibrated, RadiocarbonUncalibrated, TephraEvent, Biostrat, Other }
public enum RunOutcome { Ok, IncompleteInput, CorruptInput, BudgetExhausted, Unsolvable }

/// <summary>A domain value that keeps its original textual representation. Normalization happens only in the derived layer.</summary>
public sealed record Quantity
{
    public string Raw { get; init; } = "";
    public string Unit { get; init; } = "";
    public string? Datum { get; init; }
    public double? Normalized { get; init; }
    public string? NormalizedUnit { get; init; }
    public string? Diagnostic { get; init; }

    public bool Usable => Normalized.HasValue && Diagnostic is null;

    public static Quantity Depth(string raw, string unit, string datum) => UnitMath.NormalizeDepth(raw, unit, datum);
    public static Quantity Age(string raw, string unit, string datum) => UnitMath.NormalizeAge(raw, unit, datum);
}

public sealed record DepthBenchmark(string Id, string Label, string Unit, string Datum, double ReferenceElevationM, string Note);

public sealed record CoreSegment
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Quantity Top { get; init; }
    public required Quantity Bottom { get; init; }
    public required string DepthDatumId { get; init; }
}

/// <summary>Explicit boundary: gaps in depth coverage and unconformities are first-class model boundaries.</summary>
public sealed record SegmentBoundary
{
    public required string Id { get; init; }
    public required BoundaryKind Kind { get; init; }
    public required Quantity Depth { get; init; }
    public required string AboveSegmentId { get; init; }
    public required string BelowSegmentId { get; init; }
    public string Note { get; init; } = "";
    public double? MissingTimeMinYr { get; init; }
    public double? MissingTimeMaxYr { get; init; }
}

/// <summary>Distribution specification retained verbatim; the derived layer expands it into samples.</summary>
public sealed record DistributionSpec
{
    public string Kind { get; init; } = "empirical"; // empirical | normal | skewtail
    public double? Mean { get; init; }
    public double? Sd { get; init; }
    public double? Shape { get; init; }
    public IReadOnlyList<double> Samples { get; init; } = Array.Empty<double>();
}

public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required Quantity Depth { get; init; }
    public required Quantity Age { get; init; }
    public required string RawRepresentation { get; init; }
    public required DistributionSpec Distribution { get; init; }
    public DatingKind DatingKind { get; init; } = DatingKind.Other;
    public string LabCode { get; init; } = "";
    public double? ReservoirOffsetYr { get; init; }
    public double ReservoirOffsetUncYr { get; init; }
    public string SourceRef { get; init; } = "";
    public string SourceVersion { get; init; } = "";
}

public sealed record EventBed
{
    public required string Id { get; init; }
    public required Quantity Depth { get; init; }
    public required string CorrelationKey { get; init; }
    public Quantity? AgeConstraint { get; init; }
    public string Note { get; init; } = "";
}

public sealed record Materials
{
    public string DepthDatumId { get; init; } = "";
    public IReadOnlyList<DepthBenchmark> Benchmarks { get; init; } = Array.Empty<DepthBenchmark>();
    public IReadOnlyList<CoreSegment> Segments { get; init; } = Array.Empty<CoreSegment>();
    public IReadOnlyList<SegmentBoundary> Boundaries { get; init; } = Array.Empty<SegmentBoundary>();
    public IReadOnlyList<AgeAnchor> Anchors { get; init; } = Array.Empty<AgeAnchor>();
    public IReadOnlyList<EventBed> EventBeds { get; init; } = Array.Empty<EventBed>();
}

public sealed record Exclusion(string Id, string AnchorId, string Reason, string DecidedBy, bool Active,
    long CreatedEventSeq, long? ReversedEventSeq, string RuleVersion);

public sealed record ParameterSet(int Version, int McSamples, int MaxCandidates, double MonotonicToleranceYr,
    int RandomSeed, string ChangedBy, string Reason);

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Location { get; init; }
    public string? TargetId { get; init; }
    public string Severity { get; init; } = "error";
}

public sealed record QuantilePoint(double DepthM, double Q025, double Q50, double Q975);

public sealed record RateInterval(string SegmentId, string FromAnchorId, string ToAnchorId,
    double DepthGapM, double LowYrPerM, double MedianYrPerM, double HighYrPerM);

public sealed record SegmentCurve
{
    public required string SegmentId { get; init; }
    public required IReadOnlyList<QuantilePoint> Band { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public bool ExtrapolatedAbove { get; init; }
    public bool ExtrapolatedBelow { get; init; }
}

public sealed record ModelCandidate
{
    public required string CandidateId { get; init; }
    public required string ChoicePath { get; init; }
    public required IReadOnlyList<SegmentCurve> Curves { get; init; }
    public double AcceptedFraction { get; init; }
    public int Rank { get; init; }
    public bool EquallyOptimal { get; init; }
    public IReadOnlyList<string> ChosenAnchorIds { get; init; } = Array.Empty<string>();
}

public sealed record RunResult
{
    public required string RunId { get; init; }
    public required string InputDigest { get; init; }
    public required int ParamVersion { get; init; }
    public string? ParentRunId { get; init; }
    public required RunOutcome Outcome { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<ModelCandidate> Candidates { get; init; }
    public required IReadOnlyDictionary<string, string> SegmentStatus { get; init; }
    public long EventSeq { get; init; }
    public string CreatedUtc { get; init; } = "";
    /// <summary>Per-segment input digests so a local source change only invalidates dependent results.</summary>
    public required IReadOnlyDictionary<string, string> SegmentInputDigest { get; init; }
}

public sealed record EventRecord(long Seq, string Type, string CreatedUtc, string RuleVersion, string PayloadJson,
    string PrevDigest, string EventDigest);

public sealed record FreezeRecord(string RunId, string CandidateId, long Seq, int FreezeVersion, string CreatedUtc);

public sealed record ImpactComparison
{
    public required string ComparisonId { get; init; }
    public required string FrozenRunId { get; init; }
    public required string FrozenCandidateId { get; init; }
    public required string NewRunId { get; init; }
    public required string ProposedAnchorId { get; init; }
    public required IReadOnlyList<string> AffectedSegmentIds { get; init; }
    public required IReadOnlyList<string> UnchangedSegmentIds { get; init; }
    public required IReadOnlyList<QuantilePoint> FrozenBand { get; init; }
    public required IReadOnlyList<QuantilePoint> NewBand { get; init; }
    public long EventSeq { get; init; }
    public string Decision { get; init; } = "pending"; // pending | accepted | rejected
    public long? DecisionEventSeq { get; init; }
}
