using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth;

/// <summary>Three failure kinds must stay distinct, plus Ok and Conflict.</summary>
public enum ResultKind { Ok, BudgetExhausted, FormatCorrupt, SemanticUnsolvable, Conflict }

/// <summary>Structured, machine-readable diagnostic with a position.</summary>
public record Diagnostic(string Code, string Message, string? SegmentId, double? Depth, string? MaterialId);

/// <summary>
/// A domain quantity. The raw representation is always preserved; normalization
/// happens only in the derived layer. Unconvertible values stay in place with a
/// diagnostic instead of being silently dropped.
/// </summary>
public record Quantity(double? Value, string Raw, string Unit, string Datum, bool Convertible, string? Diagnostic)
{
    public static Quantity Of(double value, string unit, string datum) =>
        new(value, value.ToString(System.Globalization.CultureInfo.InvariantCulture), unit, datum, true, null);

    public static Quantity Unconvertible(string raw, string unit, string datum, string diagnostic) =>
        new(null, raw, unit, datum, false, diagnostic);
}

public record AgeDistribution(string Kind, double[] Values, string Unit)
{
    /// <summary>Quantile by linear interpolation over sorted values. Never collapses to a mean.</summary>
    public double Quantile(double p)
    {
        var s = Values.OrderBy(v => v).ToArray();
        if (s.Length == 0) return double.NaN;
        if (s.Length == 1) return s[0];
        var pos = p * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public double Median => Quantile(0.5);
}

// ---- Immutable materials -------------------------------------------------

public record CoreSegment(string Id, string CoreId, Quantity Top, Quantity Base, string Kind, string Facies)
{
    public bool IsBarrier => Kind is "gap" or "unconformity";
}

public record AgeAnchor(string Id, string SegmentId, Quantity Depth, AgeDistribution Age,
    Quantity? ReservoirOffset, string Source);

public record EventLayer(string Id, string SegmentId, Quantity Depth, string Name, string CorrelatedEventId);

public record Material(string Id, string Type, string Hash, string Json);

// ---- Append-only events (decisions, computations, freezes) ---------------

public record EventRecord(long Seq, string Id, string Kind, string At, string[] Parents,
    string ParamVersion, string InputHash, string Payload, string Status);

public static class Hashing
{
    public static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
