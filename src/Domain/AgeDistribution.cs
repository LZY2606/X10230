namespace ChronoCore.Domain;

/// <summary>
/// A radiocarbon / tephra / biostrat age expressed as a full distribution, never as a mean.
/// Canonical representation is yr BP, but the original textual form is retained by the material.
/// </summary>
public sealed record AgeDistribution(
    string Kind,          // "point" | "normal" | "uniform" | "samples"
    double MedianYrBp,
    double? SigmaYr,
    double? P05YrBp,
    double? P95YrBp,
    IReadOnlyList<double>? Samples = null)
{
    public double EffectiveP05 => P05YrBp ?? (SigmaYr is { } s ? MedianYrBp - 1.645 * s : MedianYrBp);
    public double EffectiveP95 => P95YrBp ?? (SigmaYr is { } s ? MedianYrBp + 1.645 * s : MedianYrBp);

    public static AgeDistribution Point(double yrBp) => new("point", yrBp, null, yrBp, yrBp);

    public static AgeDistribution Normal(double median, double sigma)
        => new("normal", median, sigma, median - 1.645 * sigma, median + 1.645 * sigma);

    public static AgeDistribution Uniform(double lo, double hi)
        => new("uniform", (lo + hi) / 2.0, null, lo, hi);

    /// <summary>Distribution from raw posterior samples (also handles multi-modal long tails).</summary>
    public static AgeDistribution FromSamples(IReadOnlyList<double> samples)
    {
        var s = samples.OrderBy(x => x).ToList();
        double Quantile(double p)
        {
            var idx = Math.Clamp((int)Math.Round(p * (s.Count - 1), MidpointRounding.AwayFromZero), 0, s.Count - 1);
            return s[idx];
        }
        return new AgeDistribution("samples", Quantile(0.5), null, Quantile(0.05), Quantile(0.95), s);
    }

    /// <summary>Tail ratio used to flag long-tail posteriors.</summary>
    public double TailSkew()
    {
        var hi = EffectiveP95 - MedianYrBp;
        var lo = MedianYrBp - EffectiveP05;
        if (hi <= 0 && lo <= 0) return 1.0;
        return Math.Max(hi, lo) / Math.Max(1.0, Math.Min(hi, lo));
    }

    public int SampleCount => Samples?.Count ?? 0;
}

/// <summary>Marine / hardwater reservoir correction applied to a radiocarbon anchor (age correction, yr).</summary>
public sealed record ReservoirOffsetSpec(
    string Id,
    string? AnchorId,        // null = applies profile-wide
    Quantity DeltaAge,       // e.g. 400 yr
    string Basis,            // "local-marine-curve", "global-average", ...
    string Raw);
