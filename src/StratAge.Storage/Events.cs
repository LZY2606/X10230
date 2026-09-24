using System.Text.Json.Serialization;

namespace StratAge.Storage;

/// <summary>
/// Append-only event. Immutable material and mutable decisions are separated:
/// MATERIAL_IMPORT is immutable; PARAM_REVISION / SOURCE_PATCH / DECISION /
/// FREEZE / VERDICT / NEW_ANCHOR are reviewer actions that create a new
/// version and never overwrite history.
/// </summary>
public sealed record StoredEvent
{
    [JsonPropertyName("eventId")] public required string EventId { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("projectId")] public required string ProjectId { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("timestampUtc")] public required string TimestampUtc { get; init; }
    [JsonPropertyName("parameterVersion")] public required string ParameterVersion { get; init; }
    [JsonPropertyName("parentEventIds")] public required List<string> ParentEventIds { get; init; }
    [JsonPropertyName("inputDigest")] public string? InputDigest { get; init; }
    [JsonPropertyName("payload")] public required JsonElement Payload { get; init; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("adopted")] public bool? Adopted { get; init; }
}

public static class EventTypes
{
    public const string MaterialImport = "MATERIAL_IMPORT";
    public const string ParamRevision = "PARAM_REVISION";
    public const string SourcePatch = "SOURCE_PATCH";
    public const string Decision = "DECISION";
    public const string Compute = "COMPUTE";
    public const string Freeze = "FREEZE";
    public const string NewAnchor = "NEW_ANCHOR";
    public const string Verdict = "VERDICT";
    public const string ArchiveJudgment = "ARCHIVE_JUDGMENT";
}

public sealed record ExportBundle
{
    [JsonPropertyName("bundleSchema")] public required string BundleSchema { get; init; }
    [JsonPropertyName("exportedUtc")] public required string ExportedUtc { get; init; }
    [JsonPropertyName("projectId")] public required string ProjectId { get; init; }
    [JsonPropertyName("events")] public required List<StoredEvent> Events { get; init; }
}
