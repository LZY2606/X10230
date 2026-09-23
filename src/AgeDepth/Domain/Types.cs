using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

public enum RunStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

public enum BoundaryKind { Continuous, Gap, Unconformity }

public record Diagnostic(string MaterialId, string? Field, string Message, string Severity)
{
    public string Position => Field is null ? MaterialId : $"{MaterialId}#{Field}";
}

/// <summary>A physical quantity: value is retained in raw form at import; normalization is derived only.</summary>
public sealed class RawQuantity
{
    public string Raw { get; set; } = "";
    public string? Unit { get; set; }
    public string? Datum { get; set; }       // depth datum or time datum
    public double? Value { get; set; }        // normalized value, null when unconvertible
    public string? NormalizedUnit { get; set; }
    public string? Diagnostic { get; set; }

    public bool Unconvertible => Value is null && !string.IsNullOrWhiteSpace(Raw);
}

public sealed class AgeDistribution
{
    public string Kind { get; set; } = "normal"; // normal | uniform | empirical
    public double? Mean { get; set; }
    public double? Sigma { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double[]? Samples { get; set; }      // full distribution is never collapsed to a mean
    public string Unit { get; set; } = "cal ka BP";
    public string TimeDatum { get; set; } = "BP 1950 CE";
}

public sealed class DepthDatum
{
    public string CoreId { get; set; } = "CORE-1";
    public RawQuantity Depth { get; set; } = new();
    public string Label { get; set; } = "";
}

public sealed class SegmentMaterial
{
    public string CoreId { get; set; } = "CORE-1";
    public string SegmentId { get; set; } = "";
    public RawQuantity Top { get; set; } = new();
    public RawQuantity Bottom { get; set; } = new();
    public string Lithology { get; set; } = "";
    public BoundaryKind Below { get; set; } = BoundaryKind.Continuous;
    public string? BoundaryNote { get; set; }
}

public sealed class AnchorMaterial
{
    public string CoreId { get; set; } = "CORE-1";
    public string AnchorId { get; set; } = "";
    public RawQuantity Depth { get; set; } = new();
    public AgeDistribution Age { get; set; } = new();
    public string Method { get; set; } = "";       // 14C planktic / tephra / ...
    public string? ReservoirOffsetId { get; set; }
    public string? EventLayerId { get; set; }
    public bool Excluded { get; set; }
    public string? ExcludeReason { get; set; }
}

public sealed class ReservoirOffset
{
    public string OffsetId { get; set; } = "";
    public RawQuantity Value { get; set; } = new(); // positive years subtracted from radiocarbon age
}

public sealed class EventLayerMaterial
{
    public string CoreId { get; set; } = "CORE-1";
    public string EventLayerId { get; set; } = "";
    public RawQuantity Depth { get; set; } = new();
    public string Label { get; set; } = "";
    public string Correlation { get; set; } = "";  // e.g. "correlated to N-Ash, dated elsewhere"
}

public sealed class ExclusionMaterial
{
    public string ExclusionId { get; set; } = "";
    public string TargetMaterialId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string DecidedBy { get; set; } = "";
}

/// <summary>Engine rules. Versioned; runs pin the version that produced them.</summary>
public sealed class RuleSet
{
    public string Version { get; set; } = "rules-1";
    public int MaxCandidates { get; set; } = 64;
    public int McSamples { get; set; } = 400;
    public double EqualScoreTolerance { get; set; } = 1e-9;
    public int MonotonicTries { get; set; } = 40;
}

// ---- derived views ----

public sealed class SectionView
{
    public required string SectionId { get; init; }
    public required double TopM { get; init; }
    public required double BottomM { get; init; }
    public required BoundaryKind LowerBoundary { get; init; }
    public required List<AnchorView> Anchors { get; init; }
    public required List<string> SegmentIds { get; init; }
}

public sealed class AnchorView
{
    public required string AnchorId { get; init; }
    public required double DepthM { get; init; }
    public required AgeDistribution Age { get; init; }
    public required string Method { get; init; }
    public string? EventLayerId { get; init; }
}

public sealed class CaseView
{
    public required string CoreId { get; init; }
    public required List<SectionView> Sections { get; init; }
    public required List<EventLayerMaterial> EventLayers { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required bool FormatCorrupt { get; init; }
}
