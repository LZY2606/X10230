using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeModel.Core;

public sealed record ProjectState(
    string ProjectId,
    CasePackage Package,
    RuleParameters Parameters,
    IReadOnlyDictionary<string, AgeAnchor> Anchors,
    IReadOnlyDictionary<string, ExclusionDecision> Exclusions,
    IReadOnlyDictionary<string, EventLayer> Events,
    IReadOnlyDictionary<string, ReservoirOffset> Offsets,
    IReadOnlyDictionary<string, string> AcceptedCorrelations,
    IReadOnlyList<EventEnvelope> EventsLog,
    string? FrozenEventId,
    string? FrozenCandidateId,
    string? BaselineArchiveEventId,
    string Branch = "main");

public static class ProjectStateFactory
{
    public static ProjectState Initial(string projectId, CasePackage package, EventEnvelope importedEvent)
    {
        var anchors = package.Anchors.ToDictionary(a => a.Id);
        var exclusions = package.Exclusions.ToDictionary(e => e.Id);
        var events = package.EventLayers.ToDictionary(e => e.Id);
        var offsets = package.ReservoirOffsets.ToDictionary(o => o.Id);
        var correlations = events.Values
            .Where(e => e.CorrelationBasis.Equals("accepted", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => e.CorrelationId, e => e.Id);

        return new ProjectState(
            projectId,
            package,
            package.Parameters,
            anchors,
            exclusions,
            events,
            offsets,
            correlations,
            new[] { importedEvent },
            null,
            null,
            null);
    }

    public static string DeterministicHash(object value)
    {
        var json = JsonSerializer.Serialize(value, ChronologyJson.Options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class ChronologyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
