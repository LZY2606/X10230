using Chrono.Core.Units;

namespace Chrono.Core.Materials;

public enum BoundaryKind { Lithologic, Unconformity, CoringGap, DuplicateDepthMarker, InversionMarker }
public enum EventBedKind { Tephra, FloodBed, Turbidite, Paleomagnetic, IsotopeSpike }
public enum ExclusionKind { LabFlag, Contamination, DuplicateAliquot, Superseded, SemanticReject, HumanReview }
public enum AnchorRole { Radiocarbon, EventTie, Biostratigraphic, Magnetostratigraphic }

public sealed record CoreSection(
    string Id,
    string Name,
    int SectionIndex,
    DepthValue Top,
    DepthValue Bottom,
    VerticalDatum Datum,
    string RawTopText,
    string RawBottomText);

public sealed record SegmentBoundary(
    string Id,
    DepthValue Depth,
    BoundaryKind Kind,
    string Label,
    string RawDepthText);

public sealed record Segment(
    string Id,
    string Label,
    int Order,
    string? TopBoundaryId,
    string? BottomBoundaryId,
    bool Unbroken);

public sealed record TimeDatum(string Code, string Name, string ReferenceYearNote);

public sealed record ReservoirCorrection(
    string Id,
    string Code,
    string CurveName,
    TimeDatum TimeDatum,
    double DeltaRCalYr,
    double DeltaRSigmaCalYr,
    string BasisNote,
    string RawDeltaRText);

public sealed record RawRadiometric(
    string LabCode,
    string Material,
    string ConventionalBpText,
    string SigmaText,
    double? ConventionalBp,
    double? SigmaBp);

public sealed record EventBed(
    string Id,
    string Name,
    EventBedKind Kind,
    DepthValue Depth,
    double ThicknessMeters,
    string CorrelationKey,
    string RawDepthText);

public sealed record Anchor(
    string Id,
    string Label,
    AnchorRole Role,
    DepthValue Depth,
    string RawDepthText,
    AgeDistribution MeasuredAge,
    AgeDistribution? CorrectedAge,
    string? ReservoirId,
    string? EventBedId,
    string? DuplicateGroupId,
    string? TieGroupId,
    RawRadiometric? Radiometric,
    string ProvenanceSourceId,
    int OrderingHint);

public sealed record SourceExclusion(
    string Id,
    string TargetAnchorId,
    ExclusionKind Kind,
    string Reason,
    string SourceRef,
    bool HumanDecision,
    string RawText);

public sealed record ChronoParameters(
    string Version,
    int MonteCarloSamples,
    long RandomSeed,
    double MinRateCmPerKa,
    double MaxRateCmPerKa,
    double GapPenaltyWeight,
    string BasisNote)
{
    public static ChronoParameters Initial => new(
        "params-v1", 2000, 20260923L, 0.0, double.PositiveInfinity, 1.0,
        "initial working rule set: monotonic within unbroken segments only");
}

public sealed record MaterialProject(
    string Id,
    string Name,
    TimeDatum DefaultTimeDatum,
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<SegmentBoundary> Boundaries,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<EventBed> EventBeds,
    IReadOnlyList<ReservoirCorrection> ReservoirCorrections,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<SourceExclusion> Exclusions,
    IReadOnlyList<RawToken> RawTokens);
