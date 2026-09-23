namespace AgeDepth.Core;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>
/// A machine-readable judgment record. Rule ids make the reasoning behind
/// "monotonic only within continuous sections", "gaps and unconformities are
/// explicit boundaries", "a distribution is never replaced by its mean" and
/// "equally good models are all retained" inspectable and replayable.
/// </summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string OwnerId,
    string Field,
    string Message,
    double Position,
    string RuleId)
{
    public static Diagnostic New(string code, DiagnosticSeverity severity, string ownerId,
        string field, string message, double position, string ruleId = RuleIds.ValueIntegrity) =>
        new(code, severity, ownerId, field, message, position, ruleId);
}

public static class RuleIds
{
    public const string ValueIntegrity = "RULE_VALUE_INTEGRITY";
    public const string MonotonicWithinSection = "RULE_MONOTONIC_WITHIN_CONTINUOUS_SECTION";
    public const string GapIsBoundary = "RULE_GAP_UNCONFORMITY_IS_EXPLICIT_BOUNDARY";
    public const string DistributionNotMean = "RULE_DISTRIBUTION_NEVER_REPLACED_BY_MEAN";
    public const string KeepEqualGood = "RULE_KEEP_ALL_EQUAL_GOOD_MODELS";
    public const string DeterministicOrder = "RULE_EQUIVALENT_INPUT_STABLE_ORDER";
    public const string LocalRecompute = "RULE_LOCAL_RECOMPUTE_ONLY_DEPENDENTS";
    public const string FreezeConcurrency = "RULE_FREEZE_OPTIMISTIC_CONCURRENCY";
}

/// <summary>The three distinct failure outcomes, never collapsed into one.</summary>
public enum FailureKind
{
    None = 0,
    /// <summary>Resource budget exhausted (candidate/sample/event limits).</summary>
    BudgetExhausted = 1,
    /// <summary>Input bytes/structure corrupt (truncated archive, bad checksum).</summary>
    FormatCorrupt = 2,
    /// <summary>Semantically unsolvable (no monotonic path, dangling references).</summary>
    SemanticUnsolvable = 3,
}

// ---------------------------------------------------------------------------
// Immutable materials. Every material has a stable identity and a content hash;
// materials are never edited in place, a change creates a new version.
// ---------------------------------------------------------------------------

public abstract record Material(string Id, string Kind)
{
    public string ContentHash { get; init; } = "";
}

public sealed record DepthDatum(
    string Id,
    string Name,
    string Reference,
    string Note) : Material(Id, "depth-datum");

public enum SegmentKind { Continuous, Gap, Unconformity }

public sealed record CoreSegment(
    string Id,
    string CoreId,
    Quantity Top,
    Quantity Bottom,
    SegmentKind Kind,
    string Facies,
    string Note) : Material(Id, "core-segment");

public enum DistributionKind { Normal, Uniform, Samples }

/// <summary>
/// An anchor age distribution. Samples are kept whole: a long tail is part of the
/// evidence and is never collapsed into a mean (RULE_DISTRIBUTION_NEVER_REPLACED_BY_MEAN).
/// </summary>
public sealed record AgeDistribution(DistributionKind Kind, double[] Values, string Raw)
{
    public static AgeDistribution Normal(double mean, double sigma, string raw) =>
        new(DistributionKind.Normal, new[] { mean, sigma }, raw);

    public static AgeDistribution Uniform(double lo, double hi, string raw) =>
        new(DistributionKind.Uniform, new[] { lo, hi }, raw);

    public static AgeDistribution OfSamples(IEnumerable<double> samples, string raw) =>
        new(DistributionKind.Samples, samples.OrderBy(v => v).ToArray(), raw);

    public double MeanEstimate => Kind switch
    {
        DistributionKind.Normal => Values[0],
        DistributionKind.Uniform => (Values[0] + Values[1]) / 2.0,
        _ => Values.Average(),
    };

    /// <summary>Deterministic quantile samples; identical inputs give identical output.</summary>
    public double[] Quantiles(int count)
    {
        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            var p = (i + 0.5) / count;
            result[i] = Quantile(p);
        }
        return result;
    }

    public double Quantile(double p)
    {
        switch (Kind)
        {
            case DistributionKind.Normal:
                return Values[0] + Values[1] * Statistics.NormalInverse(p);
            case DistributionKind.Uniform:
                return Values[0] + (Values[1] - Values[0]) * p;
            default:
            {
                var s = Values;
                if (s.Length == 1) return s[0];
                var x = p * (s.Length - 1);
                var lo = (int)Math.Floor(x);
                var hi = Math.Min(lo + 1, s.Length - 1);
                var frac = x - lo;
                return s[lo] * (1 - frac) + s[hi] * frac;
            }
        }
    }

    /// <summary>Negative log density-ish penalty of the value at quantile p.</summary>
    public double Penalty(double p, double value)
    {
        switch (Kind)
        {
            case DistributionKind.Normal:
            {
                var z = (value - Values[0]) / Math.Max(Values[1], 1e-9);
                return z * z / 2.0;
            }
            case DistributionKind.Uniform:
                return 0.0;
            default:
            {
                // Empirical: sparse regions (long tail) cost more, dense regions less.
                var s = Values;
                if (s.Length < 3) return 0.0;
                var range = Math.Max(s[^1] - s[0], 1e-9);
                var idx = Array.BinarySearch(s, value);
                if (idx < 0) idx = ~idx;
                idx = Math.Clamp(idx, 1, s.Length - 2);
                var gap = Math.Max(s[idx + 1] - s[idx - 1], 1e-9) / range;
                return Math.Log(gap * s.Length);
            }
        }
    }
}

public sealed record AgeAnchor(
    string Id,
    string LabCode,
    Quantity Depth,
    AgeDistribution Distribution,
    Quantity? ReservoirOffset,
    string MaterialDated,
    string Note) : Material(Id, "age-anchor");

public sealed record EventLayer(
    string Id,
    string Name,
    Quantity Depth,
    string? CorrelatedEvent,
    Quantity? AgeEstimate,
    string Note) : Material(Id, "event-layer");

// ---------------------------------------------------------------------------
// Derived model output. Every result records its input digest, the parameter
// version and the identities of the parent materials it depends on.
// ---------------------------------------------------------------------------

public sealed record ModelParams(
    int SamplesPerAnchor,
    int MaxCandidates,
    double EqualGoodTolerance,
    double DepthEpsilonMeters,
    int Version)
{
    public static readonly ModelParams Default = new(9, 512, 0.75, 0.005, 1);
}

public sealed record AnchorPoint(string AnchorId, double DepthMeters, double AgeCalBp, double QuantileP);

public sealed record Candidate(
    string Id,
    int Rank,
    double Score,
    bool IsEqualGood,
    string SectionId,
    List<AnchorPoint> Path);

public sealed record RateInterval(
    string Id,
    string SectionId,
    string FromAnchorId,
    string ToAnchorId,
    double TopDepthMeters,
    double BottomDepthMeters,
    double RateMetersPerKaMedian,
    double RateMetersPerKaLow,
    double RateMetersPerKaHigh);

public sealed record BandPoint(
    string SectionId,
    double DepthMeters,
    double AgeP05,
    double AgeP50,
    double AgeP95);

public sealed record SectionResult(
    string SectionId,
    double TopMeters,
    double BottomMeters,
    string TopBoundary,
    string BottomBoundary,
    List<string> AnchorIds,
    bool Solvable);

public sealed record ModelResult(
    string Id,
    FailureKind Failure,
    List<Diagnostic> Diagnostics,
    List<SectionResult> Sections,
    List<Candidate> Candidates,
    List<RateInterval> Rates,
    List<BandPoint> Band,
    string InputHash,
    int ParamsVersion,
    List<string> ParentIds,
    long ProducedAtSeq);

public static class Statistics
{
    /// <summary>Acklam's inverse normal CDF approximation (deterministic).</summary>
    public static double NormalInverse(double p)
    {
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                       1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                       6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                       -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                       3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > phigh)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var qq = p - 0.5;
        var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        var x = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(x);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] * (1 - (x - lo)) + sorted[hi] * (x - lo);
    }
}
