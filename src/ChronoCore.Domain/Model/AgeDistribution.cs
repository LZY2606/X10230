using System.Globalization;

namespace ChronoCore.Domain.Model;

public enum DistributionKind
{
    /// <summary>Gaussian by reported mean and 1-sigma, but never collapsed to the mean.</summary>
    Gaussian = 0,

    /// <summary>Asymmetric two-sided uncertainty (younger/older sigmas).</summary>
    AsymmetricGaussian = 1,

    /// <summary>Explicit quantile set, used for long-tailed radiometric results.</summary>
    EmpiricalQuantiles = 2,

    /// <summary>Uniform support over [young, old]; only bounds are known.</summary>
    Uniform = 3
}

/// <summary>
/// A radiometric age as a distribution. The reported representation is kept
/// verbatim. Quantiles are the unit of propagation: substituting the mean
/// for the distribution is explicitly disallowed (see engine diagnostics
/// code <c>DIST_MEAN_SUBSTITUTION</c>).
/// </summary>
public sealed record AgeDistribution(
    string Id,
    DistributionKind Kind,
    double Central,
    double SigmaYoung,
    double SigmaOld,
    string AgeUnit,
    TimeBasis Basis,
    double ReservoirOffset,
    string ReservoirRaw,
    IReadOnlyList<Quantile> Quantiles,
    string Raw)
{
    /// <summary>p02.5/p50/p97.5 style points; long tails are represented here,
    /// not flattened.</summary>
    public bool HasLongTail =>
        Quantiles.Any(q => q.Probability >= 0.95) &&
        Quantiles.Any(q => q.Probability <= 0.05) &&
        Quantiles.Max(q => q.Value - Central) > 3.0 * Math.Max(SigmaYoung, 1e-9);

    public double Q(double p)
    {
        var ordered = Quantiles.OrderBy(q => q.Probability).ToList();
        if (ordered.Count == 0)
            throw new InvalidOperationException("分布没有可用分位数");
        if (p <= ordered[0].Probability) return ordered[0].Value;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (p <= ordered[i].Probability)
            {
                var a = ordered[i - 1];
                var b = ordered[i];
                var f = (p - a.Probability) / (b.Probability - a.Probability);
                return a.Value + f * (b.Value - a.Value);
            }
        }
        return ordered[^1].Value;
    }

    public string DistributionFingerprint()
    {
        var qs = string.Join("|", Quantiles
            .OrderBy(q => q.Probability)
            .Select(q => q.Probability.ToString("G", CultureInfo.InvariantCulture) + ":" +
                         q.Value.ToString("G12", CultureInfo.InvariantCulture)));
        return $"{Kind}:{Central.ToString("G12", CultureInfo.InvariantCulture)}:" +
               $"{SigmaYoung.ToString("G12", CultureInfo.InvariantCulture)}:" +
               $"{SigmaOld.ToString("G12", CultureInfo.InvariantCulture)}:{AgeUnit}:{qs}";
    }
}

public readonly record struct Quantile(double Probability, double Value)
{
    public static Quantile Parse(string token)
    {
        var parts = token.Split('=', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new FormatException($"分位数必须写作 p=value，收到“{token}”");
        var prob = double.Parse(parts[0].TrimStart('p', 'P'), CultureInfo.InvariantCulture);
        if (prob is > 1 and <= 100) prob /= 100.0;
        var val = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return new Quantile(prob, val);
    }
}
