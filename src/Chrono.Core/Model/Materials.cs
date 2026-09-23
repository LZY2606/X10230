using System.Text.Json.Serialization;

namespace Chrono.Core.Model;

/// <summary>Structural meaning of a depth interval.</summary>
public enum Facing { Normal, Inverted }

/// <summary>Relationship of two adjacent segments.</summary>
public enum BoundaryKind { Continuous, Gap, Unconformity }

public enum AnchorMethod { Radiocarbon14C, TephraCorrelation, BiostratFirstOccurrence, TiedToEventLayer }

/// <summary>
/// Immutable source fact. Every fact keeps its original import fragment, a stable
/// content-derived identity and (when conversion succeeds) normalized values for the
/// derived layer. Unconvertible facts stay here with a diagnostic.
/// </summary>
public abstract record Fact(string Id, string Source, string RawFragment)
{
    [JsonIgnore] public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
}

public sealed record CoreInfo(
    string Id, string Name, string ProjectCode,
    string DepthDatumRaw, string DepthDatumCode,
    string PositiveDirectionRaw, string PositiveDirection,
    string TopDepthRaw, double? TopDepthM,
    string TotalLengthRaw, double? TotalLengthM,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment);

public sealed record SegmentRecord(
    string Id, string Label,
    string TopRaw, double? TopM,
    string BottomRaw, double? BottomM,
    string Lithology,
    string FacingRaw, Facing Facing,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment);

public sealed record BoundaryRecord(
    string Id, string AboveSegmentId, string BelowSegmentId,
    BoundaryKind Kind, string KindRaw,
    string? MissingAgeTopRaw, double? MissingAgeTopCalBp,
    string? MissingAgeBottomRaw, double? MissingAgeBottomCalBp,
    string Evidence,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment);

public sealed record AnchorRecord(
    string Id, string SampleCode, string LabCode, AnchorMethod Method, string MethodRaw,
    string DepthRaw, double? DepthM,
    string? RadiocarbonAgeBpRaw, double? RadiocarbonAgeBp,
    string? RadiocarbonSigmaRaw, double? RadiocarbonSigma,
    string CalendarAgeBpRaw, double? CalendarAgeBp,
    string CalendarSigmaRaw, double? CalendarSigma,
    string? TailWeightRaw, double? TailWeight,
    string? TailShiftBpRaw, double? TailShiftBp,
    string? TailSigmaRaw, double? TailSigma,
    string CalendarConvention, string Notes,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment)
{
    public bool IsExcluded { get; init; }
    public string? ExclusionId { get; init; }
}

public sealed record ReservoirRecord(
    string Id, string Label, string AppliesToSegmentId,
    string DeltaRRaw, double? DeltaRYears,
    string SigmaRaw, double? SigmaYears,
    string Convention,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment);

public sealed record EventLayerRecord(
    string Id, string Code, string Kind,
    string DepthRaw, double? DepthM,
    string CalendarAgeBpRaw, double? CalendarAgeBp,
    string SigmaRaw, double? SigmaYears,
    string? CorrelatedAnchorId, string CorrelationBasis,
    string Source, string RawFragment) : Fact(Id, Source, RawFragment);

public sealed record ExclusionRecord(
    string Id, string TargetId, string TargetKind, string Reason, string Basis,
    string DecidedBy, string Source, string RawFragment) : Fact(Id, Source, RawFragment);

/// <summary>The full immutable material layer plus import-time diagnostics.</summary>
public sealed record ProjectMaterials(
    string SchemaVersion,
    CoreInfo Core,
    IReadOnlyList<SegmentRecord> Segments,
    IReadOnlyList<BoundaryRecord> Boundaries,
    IReadOnlyList<AnchorRecord> Anchors,
    IReadOnlyList<ReservoirRecord> Reservoirs,
    IReadOnlyList<EventLayerRecord> EventLayers,
    IReadOnlyList<ExclusionRecord> Exclusions,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public string MaterialsVersion { get; init; } = "";
}
