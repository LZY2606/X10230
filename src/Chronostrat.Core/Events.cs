using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>
/// An append-only event. Identity is a content hash of the canonical payload,
/// so equivalent inputs get stable identities and stable ordering.
/// </summary>
public sealed record Event(
    string Id,
    long Seq,
    string Type,
    JsonObject Payload,
    string[] DependsOn,
    string? InputDigest,
    int ParamVersion,
    string RecordedAt)
{
    public static string ComputeId(string type, JsonObject payload, string salt = "")
    {
        string canonical = CanonicalJson.Serialize(payload);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + salt + "|" + canonical));
        return "evt-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

public static class EventTypes
{
    public const string MaterialImported = "MaterialImported";
    public const string ParameterRevised = "ParameterRevised";
    public const string ModelDerived = "ModelDerived";
    public const string ModelFrozen = "ModelFrozen";
    public const string FreezeRejected = "FreezeRejected";       // lost a concurrent freeze
    public const string CandidateAccepted = "CandidateAccepted";
    public const string CandidateRejected = "CandidateRejected";
    public const string LocalRecompute = "LocalRecompute";
    public const string RuleVerdict = "RuleVerdict";             // machine-readable judgment
    public const string ArchiveImported = "ArchiveImported";
}

/// <summary>Machine-readable verdict for the hard domain rules.</summary>
public static class RuleIds
{
    public const string MonotonicWithinContinuousSegment = "monotonic-within-continuous-segment";
    public const string GapIsExplicitBoundary = "gap-is-explicit-boundary";
    public const string UnconformityIsExplicitBoundary = "unconformity-is-explicit-boundary";
    public const string DistributionNotReplacedByMean = "distribution-not-replaced-by-mean";
    public const string EquallyDefensibleModelsRetained = "equally-defensible-models-retained";
}

/// <summary>Canonical JSON: sorted keys, invariant numbers — the basis of stable ids.</summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                sb.Append(val.ToJsonString());
                break;
        }
    }
}

public static class StableId
{
    public static string For(string prefix, JsonObject payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(payload)));
        return prefix + "-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
