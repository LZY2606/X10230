using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>Archive import outcomes — format corruption is its own result kind.</summary>
public abstract record ArchiveOutcome
{
    public sealed record Ok(int EventCount) : ArchiveOutcome;
    public sealed record FormatCorrupt(string Position, string Reason) : ArchiveOutcome;
}

/// <summary>
/// Export/import with integrity checking. Identities, order and provenance
/// survive a delete-and-reimport round trip; a truncated or tampered archive
/// fails as FormatCorrupt and never half-writes.
/// </summary>
public static class Archive
{
    public const string Format = "chronostrat-archive/1";

    public static string Export(IReadOnlyList<Event> events)
    {
        var arr = new JsonArray(events.Select(EventToJson).ToArray());
        string canonical = CanonicalJson.Serialize(arr);
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var root = new JsonObject
        {
            ["format"] = Format,
            ["count"] = events.Count,
            ["checksum"] = checksum,
            ["events"] = arr
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parse and validate; does not touch any store.</summary>
    public static ArchiveOutcome Validate(string text, out IReadOnlyList<Event> events)
    {
        events = Array.Empty<Event>();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text)!.AsObject();
        }
        catch (JsonException je)
        {
            return new ArchiveOutcome.FormatCorrupt($"byte={je.BytePositionInLine ?? 0}", je.Message);
        }

        if (root["format"]?.GetValue<string>() != Format)
            return new ArchiveOutcome.FormatCorrupt("header:format", "unknown or missing archive format");
        if (root["events"] is not JsonArray arr)
            return new ArchiveOutcome.FormatCorrupt("body:events", "missing events array");

        string canonical = CanonicalJson.Serialize(arr);
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (root["checksum"]?.GetValue<string>() != checksum)
            return new ArchiveOutcome.FormatCorrupt("header:checksum", "checksum mismatch; archive truncated or tampered");
        if (root["count"]?.GetValue<int>() != arr.Count)
            return new ArchiveOutcome.FormatCorrupt("header:count", "event count mismatch");

        var parsed = new List<Event>();
        for (int i = 0; i < arr.Count; i++)
        {
            try
            {
                parsed.Add(EventFromJson(arr[i]!.AsObject()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return new ArchiveOutcome.FormatCorrupt($"events[{i}]", ex.Message);
            }
        }
        // order must be preserved
        if (parsed.Select(e => e.Seq).ToArray() is { } seqs && seqs.Length > 1)
            for (int i = 1; i < seqs.Length; i++)
                if (seqs[i] <= seqs[i - 1])
                    return new ArchiveOutcome.FormatCorrupt($"events[{i}].seq", "sequence not monotonic");
        events = parsed;
        return new ArchiveOutcome.Ok(parsed.Count);
    }

    public static JsonObject EventToJson(Event e) => new()
    {
        ["id"] = e.Id,
        ["seq"] = e.Seq,
        ["type"] = e.Type,
        ["payload"] = e.Payload.DeepClone(),
        ["dependsOn"] = new JsonArray(e.DependsOn.Select(JsonValue.Create).ToArray()),
        ["inputDigest"] = e.InputDigest,
        ["paramVersion"] = e.ParamVersion,
        ["recordedAt"] = e.RecordedAt
    };

    public static Event EventFromJson(JsonObject o) => new(
        o["id"]!.GetValue<string>(),
        o["seq"]!.GetValue<long>(),
        o["type"]!.GetValue<string>(),
        o["payload"]!.AsObject(),
        o["dependsOn"] is JsonArray d ? d.Select(x => x!.GetValue<string>()).ToArray() : Array.Empty<string>(),
        o["inputDigest"]?.GetValue<string>(),
        o["paramVersion"]!.GetValue<int>(),
        o["recordedAt"]!.GetValue<string>());
}
