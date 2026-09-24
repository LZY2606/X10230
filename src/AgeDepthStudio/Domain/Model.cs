using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepthStudio.Domain;

/// <summary>
/// A domain quantity. The raw representation is preserved exactly as imported;
/// normalization into <see cref="Value"/> happens only in the derived layer.
/// Values that cannot be converted stay in place and carry a diagnostic.
/// </summary>
public sealed record Quantity(string Raw, string Unit, string Datum)
{
    public double? Value { get; init; }
    public string? Diagnostic { get; init; }

    public static Quantity Parse(string? raw, string unit, string datum, string objectId, string field)
    {
        if (raw is null || !double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return new Quantity(raw ?? "", unit, datum)
            {
                Value = null,
                Diagnostic = $"unconvertible-value: {objectId}.{field} raw='{raw}' unit='{unit}' kept in place"
            };
        }
        return new Quantity(raw, unit, datum) { Value = v };
    }
}

public enum SegmentKind { Continuous, Gap, Unconformity }

public sealed record CoreSegment(string Id, Quantity Top, Quantity Bottom, SegmentKind Kind);

/// <summary>
/// An age distribution kept as quantile points (never collapsed to a mean).
/// Quantiles are cumulative probabilities in (0,1), values in ka BP.
/// </summary>
public sealed record AgeDistribution(IReadOnlyList<double> Quantiles, IReadOnlyList<double> Values)
{
    public double At(double p)
    {
        for (var i = 0; i < Quantiles.Count; i++)
        {
            if (Quantiles[i] >= p)
            {
                if (i == 0) return Values[0];
                var t = (p - Quantiles[i - 1]) / (Quantiles[i] - Quantiles[i - 1]);
                return Values[i - 1] + t * (Values[i] - Values[i - 1]);
            }
        }
        return Values[^1];
    }

    public double Median => At(0.5);
    public double P5 => At(0.05);
    public double P95 => At(0.95);

    public AgeDistribution Shifted(double deltaKa) =>
        new(Quantiles, Values.Select(v => v + deltaKa).ToArray());
}

public sealed record AgeAnchor(
    string Id,
    Quantity Depth,
    AgeDistribution Age,
    Quantity? ReservoirOffset,
    string? EventLayerId,
    bool Excluded,
    string? ExclusionReason);

public sealed record EventLayer(string Id, Quantity Depth, string CorrelatesTo, AgeDistribution AgeRange);

public sealed record ExclusionDecision(string Id, string TargetId, string Reason, string DecidedBy);

/// <summary>
/// Immutable material layer: depth datum, segment gaps, radiometric age
/// distributions, reservoir offsets, event-layer correlations and every
/// exclusion decision are preserved as imported.
/// </summary>
public sealed record MaterialSet(
    string CoreId,
    string DepthDatum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions)
{
    public string Digest() => StableHash.Id("mat", JsonSerializer.Serialize(this, JsonUtil.Options));
}

/// <summary>
/// Mutable decision layer, kept strictly separate from the immutable materials.
/// </summary>
public sealed record DecisionSet(
    int RuleVersion,
    IReadOnlyDictionary<string, bool> AnchorAccepted,
    IReadOnlyDictionary<string, string> SourcePatches)
{
    public static readonly DecisionSet Initial = new(1, new Dictionary<string, bool>(), new Dictionary<string, string>());

    public bool IsAccepted(AgeAnchor a) => !AnchorAccepted.TryGetValue(a.Id, out var ok) || ok;
}

public sealed record Diagnostic(string Code, string Message, string? ObjectId, string? Field);

/// <summary>Machine-readable judgment emitted as an event fact.</summary>
public sealed record Judgment(string Id, string Kind, string Summary, IReadOnlyDictionary<string, string> Facts);

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public static class StableHash
{
    public static string Id(string prefix, string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"{prefix}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }
}
