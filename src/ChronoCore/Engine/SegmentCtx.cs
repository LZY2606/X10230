using ChronoCore.Domain;

namespace ChronoCore.Engine;

internal sealed class SegmentCtx
{
    public required string SegmentId { get; init; }
    public required string SourceMaterialId { get; init; }
    public double Top { get; init; }
    public double Base { get; init; }
    public BoundaryKind LowerBoundary { get; init; }
    public bool Overturned { get; init; }
    public string Lithology { get; init; } = "";
    public int AnchorCount { get; set; }
    public bool BoundaryBlocks() => LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity or BoundaryKind.Fault;
}
