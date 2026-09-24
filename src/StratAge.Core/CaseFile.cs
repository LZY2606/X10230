using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratAge.Core;

/// <summary>
/// The immutable offline case pack. This is domain material for the specific
/// narrative "core chronology ruler: depth anchors, sedimentation rates and a
/// genealogy of multiple candidate models" — not a generic table.
/// </summary>
public sealed record CaseFile
{
    [JsonPropertyName("schema")] public required string Schema { get; init; }
    [JsonPropertyName("caseId")] public required string CaseId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("narrative")] public required string Narrative { get; init; }
    [JsonPropertyName("coreId")] public required string CoreId { get; init; }
    [JsonPropertyName("depthDatum")] public required string DepthDatum { get; init; }
    [JsonPropertyName("timeBasis")] public required string TimeBasis { get; init; }
    [JsonPropertyName("referenceYear")] public int ReferenceYear { get; init; } = 1950;
    [JsonPropertyName("segments")] public required List<Segment> Segments { get; init; }
    [JsonPropertyName("boundaries")] public required List<SegmentBoundary> Boundaries { get; init; }
    [JsonPropertyName("anchors")] public required List<AgeAnchor> Anchors { get; init; }
    [JsonPropertyName("eventBeds")] public required List<EventBed> EventBeds { get; init; }
    [JsonPropertyName("reservoirOffsets")] public required List<ReservoirOffset> ReservoirOffsets { get; init; }
    [JsonPropertyName("exclusions")] public required List<Exclusion> Exclusions { get; init; }
    [JsonPropertyName("parameterRevision")] public Dictionary<string, string>? ParameterRevision { get; init; }
    [JsonPropertyName("localSourcePatch")] public Dictionary<string, string>? LocalSourcePatch { get; init; }
    [JsonPropertyName("conflictingDecisions")] public List<Dictionary<string, string>>? ConflictingDecisions { get; init; }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static CaseFile Parse(string json) =>
        JsonSerializer.Deserialize<CaseFile>(json, JsonOpts)
        ?? throw new FormatException("Case file deserialised to null (FORMAT_CORRUPT).");
}
