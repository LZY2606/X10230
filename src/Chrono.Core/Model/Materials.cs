namespace Chrono.Core.Model;

/// <summary>Kind of contact at a segment boundary.</summary>
public enum ContactKind
{
    /// <summary>Conformable, continuous: ages interpolate straight through.</summary>
    Conformable,
    /// <summary>Explicit gap/hiatus: monotonicity does NOT cross, no interpolation across it.</summary>
    Unconformity,
    /// <summary>Core recovery gap of unknown time significance.</summary>
    RecoveryGap,
}

/// <summary>
/// A stratigraphic segment of the core. Immutable source material. <see cref="RawTop"/>/<see cref="RawBottom"/>
/// retain the imported textual representation; normalized metres appear only on derived views.
/// </summary>
public sealed record Segment(
    string Id,
    string Code,
    string RawTop,
    string RawTopUnit,
    string RawBottom,
    string RawBottomUnit,
    double TopMetres,
    double BottomMetres,
    ContactKind LowerContact,
    string? ContactRaw,
    string Lithology,
    int SourceVersion);

/// <summary>
/// A radiometric age anchor. The raw fields (depth, age text, reservoir reference) are immutable;
/// the distribution is parsed at import. <see cref="Unconvertible"/> anchors stay in place with a
/// diagnostic rather than being dropped.
/// </summary>
public sealed record Anchor(
    string Id,
    string Label,
    string RawDepth,
    string RawDepthUnit,
    double? DepthMetres,
    string RawAge,
    AgeDistribution? Distribution,
    string? ReservoirOffsetId,
    string Material,
    bool Excluded,
    string? ExclusionReason,
    int SourceVersion,
    string? UnconvertibleReason = null)
{
    public bool Usable => !Excluded && DepthMetres.HasValue && Distribution is not null && UnconvertibleReason is null;
}

/// <summary>An event layer (tephra etc.) that can correlate anchors to a shared external chronology.</summary>
public sealed record EventLayer(
    string Id,
    string Code,
    string RawDepth,
    string RawDepthUnit,
    double? DepthMetres,
    string Correlation,
    string ExternalAgeReference,
    int SourceVersion);

/// <summary>Every exclusion decision is retained: nothing is silently removed.</summary>
public sealed record Exclusion(
    string Id,
    string TargetAnchorId,
    string Reason,
    string DecidedBy,
    string EventId,
    bool Active,
    int SourceVersion);
