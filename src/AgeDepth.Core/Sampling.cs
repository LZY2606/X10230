namespace AgeDepth.Core;

/// <summary>
/// Samples an <see cref="AgeDistribution"/> directly from its shape. The mean is never used
/// as a stand-in: skew-normal tails and mixture modes are propagated into uncertainty bands.
/// </summary>
public static class DistributionSampler
{
    /// <param name="rng">Deterministic, seeded from the input digest.</param>
    /// <param name="d">Distribution in canonical yr BP1950.</param>
    public static double Sample(AgeDistribution d, Random rng)
    {
        switch (d.Kind)
        {
            case DistributionKind.Normal:
            {
                var (mu, sigma) = (d.Parameters[0], Math.Max(d.Parameters[1], 1e-9));
                return mu + sigma * Determinism.InverseNormal(rng.NextDouble());
            }
            case DistributionKind.LogNormal:
            {
                var (mu, sigma) = (d.Parameters[0], Math.Max(d.Parameters[1], 1e-9));
                return Math.Exp(mu + sigma * Determinism.InverseNormal(rng.NextDouble()));
            }
            case DistributionKind.Uniform:
                return d.Parameters[0] +
                       (d.Parameters[1] - d.Parameters[0]) * rng.NextDouble();
            case DistributionKind.SkewNormal:
            {
                var (xi, omega, alpha) = (d.Parameters[0],
                    Math.Max(d.Parameters[1], 1e-9), d.Parameters[2]);
                // Azzalini skew-normal via two correlated normals (long tail when alpha>0).
                var u0 = Determinism.InverseNormal(rng.NextDouble());
                var v = Determinism.InverseNormal(rng.NextDouble());
                var delta = alpha / Math.Sqrt(1 + alpha * alpha);
                var z = delta * Math.Abs(u0) + Math.Sqrt(1 - delta * delta) * v;
                return xi + omega * z;
            }
            case DistributionKind.Mixture:
            {
                // Parameters: [weight0, mean0, sd0, weight1, mean1, sd1, ...]
                var p = d.Parameters;
                var modes = p.Count / 3;
                var total = 0.0;
                for (var i = 0; i < modes; i++) total += p[i * 3];
                var pick = rng.NextDouble() * total;
                var acc = 0.0;
                for (var i = 0; i < modes; i++)
                {
                    acc += p[i * 3];
                    if (pick <= acc)
                    {
                        var mean = p[i * 3 + 1];
                        var sd = Math.Max(p[i * 3 + 2], 1e-9);
                        return mean + sd * Determinism.InverseNormal(rng.NextDouble());
                    }
                }
                var last = (modes - 1) * 3;
                return p[last + 1] + Math.Max(p[last + 2], 1e-9) *
                    Determinism.InverseNormal(rng.NextDouble());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(d));
        }
    }

    /// <summary>Quantile of a sorted sample list.</summary>
    public static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        var pos = Math.Clamp(q, 0, 1) * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
