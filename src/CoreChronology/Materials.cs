namespace CoreChronology;

public enum GapKind { Gap, Unconformity }
public enum AnchorKind { RadiocarbonCalibrated, TephraTie, Biohorizon }

/// <summary>Layer shared by all immutable input materials.</summary>
public abstract record Material(string Id)
{
    /// <summary>Original imported fragment, retained verbatim for audit.</summary>
    public required string SourceRef { get; init; }
    public required int SourceLine { get; init; }
}

public sealed record DepthDatum(
    string Id,
    Measure Depth,
    string CoordinateBasis,
    string OrientationConvention) : Material(Id);

public sealed record SegmentGap(
    string Id,
    Measure TopDepth,
    Measure BottomDepth,
    GapKind Kind,
    string Note) : Material(Id);

public sealed record AgeAnchor(
    string Id,
    Measure Depth,
    AgeDistribution Distribution,
    AnchorKind Kind,
    string? ReservoirOffsetId,
    string? EventLayerId,
    bool Optional) : Material(Id);

public sealed record ReservoirOffset(
    string Id,
    Measure Depth,
    Measure Offset,
    bool Applied) : Material(Id);

public sealed record EventLayer(
    string Id,
    Measure Depth,
    string Label,
    bool GlobalCorrelation) : Material(Id);

/// <summary>An exclusion never deletes the material; it only records the decision.</summary>
public sealed record Exclusion(
    string MaterialId,
    string ReasonCode,
    string Detail,
    string DecidedBy,
    string EventId)
{
    public string Id => $"exc-{MaterialId}@{EventId}";
}
