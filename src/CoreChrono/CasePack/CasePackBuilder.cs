using System.Text;
using System.Text.Json;
using CoreChrono.Domain;

namespace CoreChrono.CasePack;

/// <summary>Builds the dedicated offline domain case pack:
/// "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// Not a generic table — every item is core-chronology evidence.</summary>
public static class CasePackBuilder
{
    public const string SchemaVersion = "corechrono.case/1.0";

    public static EvidenceBundle Baseline()
    {
        var datum = new DepthDatum
        {
            Id = "DAT-MBSF",
            Name = "metres below sea floor (synthetic teaching core)",
            VerticalDirection = "down-positive",
            NormalizedUnit = "m",
        };

        var segments = new List<Segment>
        {
            new()
            {
                Id = "S1", Label = "Upper hemipelagite", Ordering = 1,
                TopDepth = Measure.Depth(0.00, "0.00", datum: "mbsf"),
                BottomDepth = Measure.Depth(4.20, "4.20", datum: "mbsf"),
                UpperBoundary = BoundaryKind.Conformable,
                LowerBoundary = BoundaryKind.Conformable,
                SourceNote0()
            },
            new()
            {
                Id = "S2", Label = "Tephra-bearing mud", Ordering = 1,
                TopDepth = Measure.Depth(4.20, "4.20", datum: "mbsf"),
                BottomDepth = Measure.Depth(9.80, "9.80", datum: "mbsf"),
                UpperBoundary = BoundaryKind.Conformable,
                LowerBoundary = BoundaryKind.Gap,
                BoundaryNote = "hiatus: ~missing section at 9.80 mbsf; ages must not interpolate across",
            },
            new()
            {
                Id = "S3", Label = "Overturned turbidite slice", Ordering = -1,
                TopDepth = Measure.Depth(10.60, "10.60", datum: "mbsf"),
                BottomDepth = Measure.Depth(13.40, "13.40", datum: "mbsf"),
                UpperBoundary = BoundaryKind.Unconformity,
                LowerBoundary = BoundaryKind.Fault,
                BoundaryNote = "unconformable base over S2; thrust-faulted, structurally overturned block",
            },
            new()
            {
                Id = "S4", Label = "Lower contourite", Ordering = 1,
                TopDepth = Measure.Depth(13.90, "13.90", datum: "mbsf"),
                BottomDepth = Measure.Depth(18.00, "18.00", datum: "mbsf"),
                UpperBoundary = BoundaryKind.Fault,
                LowerBoundary = BoundaryKind.Conformable,
                BoundaryNote = "fault contact with S3; no monotonic claim across the fault",
            },
        };
