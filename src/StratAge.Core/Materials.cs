using System.Text.Json.Serialization;

namespace StratAge.Core;

public enum BoundaryKind { Gap, Unconformity, Fault, CoreBreak }

/// <summary>An explicit boundary across which age interpolation is forbidden.</summary>
public sealed record SegmentBoundary
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("atDepthRaw")] public required string AtDepthRaw { get; init; }
    [JsonPropertyName("kind")] public required BoundaryKind Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonIgnore] public Quant? Depth { get; set; }
}

/// <summary>A continuous depositional section between two explicit boundaries.</summary>
public sealed record Segment
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("topDepthRaw")] public required string TopDepthRaw { get; init; }
    [JsonPropertyName("baseDepthRaw")] public required string BaseDepthRaw { get; init; }
    [JsonPropertyName("overturned")] public bool Overturned { get; init; }
    [JsonPropertyName("boundaryBelow")] public BoundaryKind? BoundaryBelow { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonIgnore] public Quant? Top { get; set; }
    [JsonIgnore] public Quant? Base { get; set; }
}

public enum AgeAnchorKind { Radiocarbon, Tephra, Bioevent, Magnetostrat, UTh, Other }

/// <summary>
/// An immutable age control point. The raw source is preserved; the derived
/// distribution lives separately so nothing is normalised destructively.
/// </summary>
public sealed record AgeAnchor
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("segmentId")] public required string SegmentId { get; init; }
    [JsonPropertyName("depthRaw")] public required string DepthRaw { get; init; }
    [JsonPropertyName("ageRaw")] public required string AgeRaw { get; init; }
    [JsonPropertyName("kind")] public required AgeAnchorKind Kind { get; init; }
    [JsonPropertyName("sourceRef")] public required string SourceRef { get; init; }
    [JsonPropertyName("laboratoryCode")] public string? LaboratoryCode { get; init; }
    [JsonPropertyName("reservoirOffsetId")] public string? ReservoirOffsetId { get; init; }
    [JsonPropertyName("eventBedId")] public string? EventBedId { get; init; }

    // Distribution supplied by the source pack (ka BP). Never replaced by a mean.
    [JsonPropertyName("distribution")] public AgeDistribution? Distribution { get; init; }
    [JsonPropertyName("ageSigmaRaw")] public string? AgeSigmaRaw { get; init; }

    // Alternate mutually-exclusive anchors share a non-null tieGroup.
    [JsonPropertyName("tieGroup")] public string? TieGroup { get; init; }

    [JsonPropertyName("excluded")] public bool Excluded { get; init; }
    [JsonPropertyName("excludeReason")] public string? ExcludeReason { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }

    [JsonIgnore] public Quant? Depth { get; set; }
    [JsonIgnore] public Quant? Age { get; set; }
    [JsonIgnore] public Quant? AgeSigma { get; set; }
}

/// <summary>A dated event bed (e.g. tephra) that correlates anchors across cores.</summary>
public sealed record EventBed
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("ageRaw")] public required string AgeRaw { get; init; }
    [JsonPropertyName("ageSigmaRaw")] public string? AgeSigmaRaw { get; init; }
    [JsonPropertyName("correlationNote")] public string? CorrelationNote { get; init; }
    [JsonIgnore] public Quant? Age { get; set; }
    [JsonIgnore] public Quant? AgeSigma { get; set; }
}

/// <summary>A retained exclusion decision — never silently discarded.</summary>
public sealed record Exclusion
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("targetId")] public required string TargetId { get; init; }
    [JsonPropertyName("targetKind")] public required string TargetKind { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("ruleId")] public required string RuleId { get; init; }
    [JsonPropertyName("keptRaw")] public required string KeptRaw { get; init; }
}

/// <summary>A directed change made by a reviewer; produces a new event/version.</summary>
public sealed record ReviewDecision
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("targetId")] public required string TargetId { get; init; }
    [JsonPropertyName("parameterVersion")] public required string ParameterVersion { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("payload")] public Dictionary<string, string> Payload { get; init; } = new();
}
