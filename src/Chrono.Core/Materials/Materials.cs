using System.Collections.Generic;
using Chrono.Core.Quantities;

namespace Chrono.Core.Materials;

/// <summary>
/// Immutable source material. Once imported these records never change; corrections arrive as new
/// records that supersede them. Raw representation is always preserved alongside any normalization.
/// </summary>
public abstract record Material(string Id, string Kind, string SourcePath, string SourceFile, int Line)
{
    /// <summary>Diagnostics produced while normalizing this record (e.g. unconvertible units).</summary>
    public List<Diagnostic> Diagnostics { get; } = new();
    /// <summary>Excluded / inactive materials remain in the store; the exclusion is a separate decision.</summary>
    public bool Superseded { get; init; }
    public string? SupersededBy { get; init; }
}

/// <summary>Depth datum + direction declaration for a core. Exactly one active datum per core.</summary>
public sealed record DepthDatumMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, string DatumName, DepthDirection Direction)
    : Material(Id, "depth_datum", SourcePath, SourceFile, Line);

public enum SegmentStructure { Continuous = 0, Faulted = 1, Overturned = 2 }

/// <summary>One cored interval in the raw source units. Depth monotonicity applies only within
/// continuous segments; faulted/overturned intervals are explicit boundaries, never interpolated.</summary>
public sealed record CoreSegmentMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, string Name,
    string? TopRaw, string? BottomRaw, string DepthUnit, string DatumName, DepthDirection Direction,
    SegmentStructure Structure,
    Depth? TopCm, Depth? BottomCm)
    : Material(Id, "core_segment", SourcePath, SourceFile, Line);

public enum GapKind { Unconformity = 0, MissingCore = 1, Hiatus = 2 }

/// <summary>Depth gap / unconformity: an explicit boundary between age-depth tracks.</summary>
public sealed record GapMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, GapKind GapType, string? DepthRaw, string DepthUnit,
    string DatumName, DepthDirection Direction, string Note,
    Depth? AtCm)
    : Material(Id, "gap", SourcePath, SourceFile, Line);

/// <summary>Radiometric date observation with its full age distribution and lab provenance.</summary>
public sealed record RadiometricDateMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, string LabCode, string? DepthRaw, string DepthUnit,
    string DatumName, DepthDirection Direction,
    IAgeDistribution? MeasuredAge, string? ReservoirOffsetId,
    Depth? AtCm)
    : Material(Id, "radiometric_date", SourcePath, SourceFile, Line)
{
    public bool Excluded { get; init; }
    public string? ExclusionReason { get; init; }
}

public sealed record ReservoirMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, string Name, IAgeDistribution OffsetDistribution, bool Enabled)
    : Material(Id, "reservoir_offset", SourcePath, SourceFile, Line);

/// <summary>Tie between an event layer (tephra etc.) in this core and a dated occurrence elsewhere.</summary>
public sealed record EventTieMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string CoreId, string EventCode, string? DepthRaw, string DepthUnit,
    string DatumName, DepthDirection Direction,
    IAgeDistribution? CorrelatedAge, string CorrelationBasis,
    Depth? AtCm)
    : Material(Id, "event_tie", SourcePath, SourceFile, Line)
{
    public bool Excluded { get; init; }
    public string? ExclusionReason { get; init; }
    /// <summary>When ties contradict one another, they share a correlation group; only one per group
    /// is usable in any candidate, but all remain stored and visible.</summary>
    public string? CorrelationGroup { get; init; }
}

/// <summary>All exclusion decisions are themselves retained records, never silent row deletions.</summary>
public sealed record ExclusionDecisionMaterial(
    string Id, string SourcePath, string SourceFile, int Line,
    string TargetMaterialId, string Reason, string DecidedBy)
    : Material(Id, "exclusion_decision", SourcePath, SourceFile, Line);
