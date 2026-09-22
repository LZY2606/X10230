using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>Deterministic, content-addressed identity. Export/import keeps ids stable.</summary>
public static class StableId
{
    public static string For(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }
}

/// <summary>Canonical JSON (sorted keys, invariant numbers) used for digests and ids.</summary>
public static class Canonical
{
    public static string Of(JsonNode? node)
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
                var first = true;
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
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                if (val.TryGetValue<double>(out var d))
                    sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(val.ToJsonString());
                break;
        }
    }

    public static string DigestOf(JsonNode? node) => StableId.For("dgst", Of(node));
}

/// <summary>A measured quantity. Raw text is always preserved; normalization happens in the derived layer.</summary>
public sealed record Quantity(double Value, string Unit, string Datum, string Raw);

public sealed record Quantile(double P, double Value);

/// <summary>Age distribution kept as quantiles (and raw text). The mean is never substituted.</summary>
public sealed record AgeDistribution(string Kind, string Unit, IReadOnlyList<Quantile> Quantiles, string Raw)
{
    public double At(double p)
    {
        var qs = Quantiles.OrderBy(q => q.P).ToList();
        if (qs.Count == 0) throw new InvalidOperationException("empty distribution");
        if (p <= qs[0].P) return qs[0].Value;
        if (p >= qs[^1].P) return qs[^1].Value;
        for (var i = 0; i < qs.Count - 1; i++)
        {
            if (p >= qs[i].P && p <= qs[i + 1].P)
            {
                var t = (p - qs[i].P) / (qs[i + 1].P - qs[i].P);
                return qs[i].Value + t * (qs[i + 1].Value - qs[i].Value);
            }
        }
        return qs[^1].Value;
    }

    public double Median => At(0.5);
    public double P025 => At(0.025);
    public double P975 => At(0.975);
}

public static class Units
{
    public const string CanonicalDepthUnit = "m";
    public const string CanonicalAgeUnit = "yr BP";
    public const string CanonicalDatum = "mudline";

    public static bool TryDepthToMeters(double value, string unit, out double meters)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "m": case "meter": case "meters": meters = value; return true;
            case "cm": meters = value / 100.0; return true;
            case "mm": meters = value / 1000.0; return true;
            case "ft": case "feet": meters = value * 0.3048; return true;
            default: meters = 0; return false;
        }
    }

    public static bool TryAgeToYears(double value, string unit, out double years)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "yr bp": case "yrbp": case "a bp": case "cal yr bp": years = value; return true;
            case "kyr bp": case "kyr": case "ka": years = value * 1000.0; return true;
            default: years = 0; return false;
        }
    }
}

// ---- Immutable materials (imported, never edited in place) ----

public sealed record DepthDatum(string Id, string Name, double OffsetMeters, string Raw);

public sealed record CoreSegment(string Id, string CoreId, Quantity Top, Quantity Bottom, bool Overturned, string Raw);

/// <summary>Explicit boundary between segments: "gap" (missing section) or "unconformity" (hiatus).</summary>
public sealed record Boundary(string Id, string Kind, Quantity Depth, string Note, string Raw);

public sealed record AgeAnchor(
    string Id, Quantity Depth, AgeDistribution Age,
    Quantity ReservoirOffset, string Source, string Raw);

public sealed record EventLayer(
    string Id, Quantity Depth, string Name, string CorrelatesWith,
    AgeDistribution? CorrelatedAge, string Raw);

// ---- Modifiable decisions (layered over materials, each is an event) ----

public sealed record ExclusionDecision(
    string Id, string AnchorId, bool Excluded, string Reason, string DecidedBy, string Raw);

public sealed record RulesParams(
    string Version,
    double ColocateToleranceM = 0.005,
    double MonotonicToleranceYears = 1e-6,
    int MaxSubsetsPerSegment = 128,
    int MaxTotalCandidates = 256)
{
    public static readonly RulesParams Default = new("rules/1.0");
}

public sealed record Diagnostic(string Code, string Path, string Message, string Severity = "error");
