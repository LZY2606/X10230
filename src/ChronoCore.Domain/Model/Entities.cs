namespace ChronoCore.Domain.Model;

/// <summary>A radiometric age control point at a core depth.</summary>
public sealed record AgeAnchor(
    string Id,
    string Label,
    DepthRef Depth,
    AgeDistribution Age,
    string Method,
    string SourceMaterialId,
    int SourceLine,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Stable identity for cross-import matching and provenance joins;
    /// independent of any derived candidate.</summary>
    public string MaterialKey =>
        Id + "|" + SourceMaterialId + "|" + Depth.CanonicalKey + "|" + Age.DistributionFingerprint();
}

public enum BoundaryKind
{
    ContinuousTop = 0,
    SegmentGap = 1,
    Unconformity = 2,
    CoreBreak = 3,
    ContinuousBottom = 4
}

/// <summary>An explicit boundary. Gaps and unconformities are real edges: age
/// monotonicity is never enforced across them.</summary>
public sealed record Boundary(
    string Id,
    BoundaryKind Kind,
    DepthRef Depth,
    double? MissingIntervalDown,
    string Raw,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>An ordered physical interval of the core between two explicit
/// boundaries. Bedding is represented by the order of these intervals.</summary>
public sealed record CoreSegment(
    string Id,
    int Sequence,
    DepthRef Top,
    DepthRef Bottom,
    string Lithology,
    string Raw,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>An event layer (tephra, turbidite, biostrat event...) that can tie
/// sections together.</summary>
public sealed record EventLayer(
    string Id,
    string CorrelationId,
    DepthRef Depth,
    double? Thickness,
    string Kind,
    double? TiedAge,
    string TiedAgeBasis,
    string Raw,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>An excluded material/anchor with the full reason. Exclusions are
/// retained as first-class evidence, never silently dropped.</summary>
public sealed record Exclusion(
    string Id,
    string TargetAnchorOrMaterialId,
    string ReasonCode,
    string Rationale,
    int SourceLine,
    string Raw);

/// <summary>A maximally continuous run of core segments: age must be
/// non-decreasing (oldest downward) only within one section.</summary>
public sealed record ContinuousSection(
    string SectionId,
    int Order,
    IReadOnlyList<string> SegmentIds,
    DepthRef Top,
    DepthRef Bottom);

/// <summary>Immutable imported package: every category the reviewers verify
/// first is preserved here.</summary>
public sealed record CorePackage(
    string CaseId,
    string CaseVersion,
    DepthDatum Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> Events,
    IReadOnlyList<Exclusion> Exclusions,
    IReadOnlyList<RawSourceLine> RawLines,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record RawSourceLine(int Line, string Text, string MaterialId);
