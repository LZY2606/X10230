using ChronoCore.Quantities;

namespace ChronoCore.Domain;

/// <summary>Kind of a boundary between continuous core sections.</summary>
public enum GapKind
{
    None = 0,
    Gap = 1,           // 深度缺口（缺芯/缺失深度区间），显式边界
    Unconformity = 2,  // 不整合（深度连续或接近但时间有跳跃），显式边界
    Inverted = 3       // 倒置层段（深度顺序倒置），结构性无效输入
}

/// <summary>Depth reference frame (datum), e.g. mbsf / mcd. Immutable material.</summary>
public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Code { get; init; }        // mbsf, mcd ...
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
}

/// <summary>A continuous core section. Sections are separated by explicit boundaries.</summary>
public sealed record CoreSection
{
    public required string Id { get; init; }
    public required string DatumId { get; init; }
    public required Measure TopDepth { get; init; }
    public required Measure BaseDepth { get; init; }
    public string? Lithology { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
    /// <summary>Boundary below this section, if any.</summary>
    public GapKind LowerBoundary { get; init; } = GapKind.None;
    /// <summary>Material id of the gap/unconformity describing the lower boundary.</summary>
    public string? BoundaryId { get; init; }
    /// <summary>Free-text reason for the boundary (原始表示保留).</summary>
    public string? BoundaryReason { get; init; }
    public string? DiagnosticCode { get; init; }
}

/// <summary>An explicit boundary: a depth gap or an unconformity / inversion.</summary>
public sealed record GapRecord
{
    public required string Id { get; init; }
    public string? UpperSectionId { get; init; }
    public string? LowerSectionId { get; init; }
    public required GapKind Kind { get; init; }
    /// <summary>Missing depth interval [from,to] when a depth gap.</summary>
    public Measure? GapFrom { get; init; }
    public Measure? GapTo { get; init; }
    /// <summary>Minimum missing time across an unconformity, distribution-aware material.</summary>
    public Measure? MinimumMissingTime { get; init; }
    public required string RawEvidence { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
}
