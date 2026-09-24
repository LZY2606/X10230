namespace Chrono.Core;

public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Unit { get; init; } = "m";
    public required string Direction { get; init; } = "down-positive";
}

public sealed record Segment
{
    public required string Id { get; init; }
    public required int Ordinal { get; init; }
    public required string Name { get; init; }
    public required RawQuantity Top { get; init; }
    public required RawQuantity Bottom { get; init; }
    public required string BoundaryBelow { get; init; } // continuous | gap | unconformity
    public string? Notes { get; init; }
    public double TopM => Top.NormalizedValue ?? double.NaN;
    public double BottomM => Bottom.NormalizedValue ?? double.NaN;
}

public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string AppliesTo { get; init; } // anchor id or material class
    public required DistributionSpec Offset { get; init; }
    public required string Basis { get; init; }
}

public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required RawQuantity Depth { get; init; }
    public required DistributionSpec Age { get; init; }
    public required string Method { get; init; }
    public string? MaterialClass { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public string? LabCode { get; init; }
    public string? Notes { get; init; }
    public double DepthM => Depth.NormalizedValue ?? double.NaN;
}

public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required RawQuantity Depth { get; init; }
    public required string Name { get; init; }
    public DistributionSpec? CorrelatedAge { get; init; }
    public string? CorrelationBasis { get; init; }
    public double DepthM => Depth.NormalizedValue ?? double.NaN;
}

public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetAnchorId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string DecidedAt { get; init; }
}

public sealed record HumanDecision
{
    public required string Id { get; init; }
    public required string Kind { get; init; } // exclude-anchor | accept-model | reject-model | adjust-params | note
    public required string Payload { get; init; } // json
    public required string DecidedBy { get; init; }
    public required string DecidedAt { get; init; }
}

/// <summary>All immutable material plus the current decision layer for one computation.</summary>
public sealed record MaterialSet
{
    public required DepthDatum Datum { get; init; }
    public required List<Segment> Segments { get; init; }
    public required List<AgeAnchor> Anchors { get; init; }
    public required List<ReservoirOffset> Reservoirs { get; init; }
    public required List<EventLayer> EventLayers { get; init; }
    public required List<ExclusionDecision> Exclusions { get; init; }
}

public sealed record RuleParams
{
    public required string Version { get; init; }
    public int CandidateBudget { get; init; } = 64;
    public int QuantileGrid { get; init; } = EffectiveDistribution.GridSize;
    public double DepthGridStepM { get; init; } = 0.1;
    public bool IncludeEventLayerCorrelations { get; init; } = true;
    public double MonotonicToleranceKa { get; init; } = 1e-9;
    public static RuleParams Default(string version) => new() { Version = version };
}

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required string Severity { get; init; } // info | warning | error
    public required string Message { get; init; }
    public string? Location { get; init; } // json pointer / entity id
    public string? ObjectId { get; init; }
}

public static class Diag
{
    public static Diagnostic Info(string code, string msg, string? obj = null, string? loc = null) =>
        new() { Code = code, Severity = "info", Message = msg, ObjectId = obj, Location = loc };
    public static Diagnostic Warn(string code, string msg, string? obj = null, string? loc = null) =>
        new() { Code = code, Severity = "warning", Message = msg, ObjectId = obj, Location = loc };
    public static Diagnostic Error(string code, string msg, string? obj = null, string? loc = null) =>
        new() { Code = code, Severity = "error", Message = msg, ObjectId = obj, Location = loc };
}

public sealed record InputDigest
{
    public required string MaterialHash { get; init; }
    public required string ParamsVersion { get; init; }
    public required string ParentEvent { get; init; }
    public required string CombinedHash { get; init; }
}
