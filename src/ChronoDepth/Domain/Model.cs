using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoDepth.Domain;

public enum BoundaryKind { Continuous, Gap, Unconformity }
public enum DiagSeverity { Info, Warning, Error }

/// <summary>Machine-readable diagnostic; always carries a location.</summary>
public sealed record Diagnostic(string Code, string Message, string Location, DiagSeverity Severity);

/// <summary>
/// A domain quantity. The raw textual representation is preserved at import;
/// normalization happens only in the derived layer. Values that cannot be
/// converted stay in place with a diagnostic instead of being dropped.
/// </summary>
public sealed record Quantity(string Raw, string Unit, string Datum)
{
    public double? Value =>
        double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static Quantity Of(double value, string unit, string datum) =>
        new(value.ToString("G17", CultureInfo.InvariantCulture), unit, datum);
}

/// <summary>
/// A radiometric age distribution. Stored as samples (ka BP); it is never
/// collapsed to a mean. Quantiles are derived on demand.
/// </summary>
public sealed record AgeDistribution
{
    public required double[] Samples { get; init; }

    public double Quantile(double p)
    {
        var s = Samples.OrderBy(x => x).ToArray();
        if (s.Length == 0) return double.NaN;
        var pos = p * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public double Median => Quantile(0.5);
    public double P5 => Quantile(0.05);
    public double P95 => Quantile(0.95);

    /// <summary>Long-tail detector: upper tail much heavier than lower tail.</summary>
    public bool IsLongTail
    {
        get
        {
            var lower = Math.Abs(Quantile(0.5) - Quantile(0.05));
            var upper = Math.Abs(Quantile(0.95) - Quantile(0.5));
            return lower > 1e-9 && upper / lower > 3.0;
        }
    }

    public static AgeDistribution Normal(double mean, double sigma, int n = 101, int seed = 7)
    {
        var rng = new Random(seed);
        var samples = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = 1.0 - rng.NextDouble();
            samples[i] = mean + sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return new AgeDistribution { Samples = samples };
    }
}

// ---------- Immutable material layer ----------

public sealed record Segment(string Id, string Name, double TopM, double BottomM, string DepthDatum);

public sealed record Boundary(
    string Id, string AfterSegmentId, BoundaryKind Kind,
    double? GapTopM, double? GapBottomM, string Note);

public sealed record Anchor(
    string Id, string SegmentId, Quantity Depth, AgeDistribution Age,
    double ReservoirOffsetYr, string Source);

public sealed record EventLayer(
    string Id, string Name, double DepthM, string CorrelatedTo, Quantity? Age);

/// <summary>A recorded exclusion decision over an anchor (kept, never deleted).</summary>
public sealed record ExclusionDecision(string AnchorId, bool Excluded, string Reason, string DecidedBy);

/// <summary>Full immutable material snapshot for one case version.</summary>
public sealed record MaterialSet(
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers)
{
    public string Digest()
    {
        var json = JsonSerializer.Serialize(this, JsonUtil.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
