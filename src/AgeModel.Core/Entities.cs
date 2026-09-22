namespace AgeModel.Core;

/// <summary>Boundary explicitly declared below a section. Gaps and unconformities are explicit boundaries.</summary>
public enum BoundaryKind { None, Gap, Unconformity }

/// <summary>Immutable material: a core section with datum-carrying depths.</summary>
public sealed record CoreSection
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Quantity Top { get; init; }
    public required Quantity Base { get; init; }
    public BoundaryKind BoundaryBelow { get; init; } = BoundaryKind.None;
    public string? BoundaryNote { get; init; }
    /// <summary>Provenance: which import / case-pack revision supplied this.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// A radiometric age distribution. It is never replaced by its mean:
/// computations use quantiles/samples of the full distribution.
/// </summary>
public sealed record AgeDistribution
{
    /// <summary>"gaussian" | "samples" | "uniform"</summary>
    public required string Kind { get; init; }
    public double? Mean { get; init; }   // ka, datum of the owning anchor
    public double? Sd { get; init; }
    public double[]? Samples { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }

    public static AgeDistribution Gaussian(double mean, double sd) =>
        new() { Kind = "gaussian", Mean = mean, Sd = sd };
    public static AgeDistribution OfSamples(double[] samples) =>
        new() { Kind = "samples", Samples = samples.OrderBy(x => x).ToArray() };
    public static AgeDistribution Uniform(double min, double max) =>
        new() { Kind = "uniform", Min = min, Max = max };

    /// <summary>Deterministic quantile of the distribution (ka).</summary>
    public double Quantile(double p)
    {
        switch (Kind)
        {
            case "gaussian":
                return Mean!.Value + Sd!.Value * InverseNormalCdf(p);
            case "uniform":
                return Min!.Value + p * (Max!.Value - Min!.Value);
            case "samples":
                var s = Samples!;
                if (s.Length == 1) return s[0];
                var pos = p * (s.Length - 1);
                var lo = (int)Math.Floor(pos);
                var hi = Math.Min(lo + 1, s.Length - 1);
                return s[lo] + (pos - lo) * (s[hi] - s[lo]);
            default:
                throw new InvalidOperationException($"unknown distribution kind {Kind}");
        }
    }

    public (double Lo, double Hi) Support(double tailP = 0.001) => (Quantile(tailP), Quantile(1 - tailP));

    /// <summary>Long right tail: upper spread dominates lower spread.</summary>
    public bool IsLongTail()
    {
        var q50 = Quantile(0.5);
        var lower = q50 - Quantile(0.05);
        var upper = Quantile(0.95) - q50;
        return lower > 0 && upper / lower > 2.5;
    }

    /// <summary>Shift by a reservoir offset (ka). Returns a derived distribution; the raw one is untouched.</summary>
    public AgeDistribution Shift(double offsetKa) => Kind switch
    {
        "gaussian" => this with { Mean = Mean + offsetKa },
        "uniform" => this with { Min = Min + offsetKa, Max = Max + offsetKa },
        "samples" => this with { Samples = Samples!.Select(x => x + offsetKa).OrderBy(x => x).ToArray() },
        _ => this,
    };

    private static double InverseNormalCdf(double p)
    {
        // Acklam's approximation, deterministic.
        if (p <= 0) return -10;
        if (p >= 1) return 10;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double pl = 0.02425, ph = 1 - 0.02425;
        if (p < pl)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > ph)
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
}

/// <summary>Immutable material: an age anchor with a full distribution and optional reservoir offset.</summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string LabCode { get; init; }
    public required Quantity Depth { get; init; }
    public required AgeDistribution Distribution { get; init; }
    /// <summary>Reservoir offset (储层偏移), ka in the anchor's age datum. Applied only in the derived layer.</summary>
    public Quantity? ReservoirOffset { get; init; }
    public string? EventLayerId { get; init; }
    public required string Source { get; init; }
}

/// <summary>Immutable material: an event layer with external correlation.</summary>
public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Quantity Depth { get; init; }
    public string? CorrelatesTo { get; init; }
    public required string Source { get; init; }
}

/// <summary>Mutable decision: exclusion of an anchor. Both adopted and rejected decisions are preserved.</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string AnchorId { get; init; }
    public required bool Excluded { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string At { get; init; }
    /// <summary>Whether this decision was adopted into the active rule set.</summary>
    public bool Adopted { get; init; } = true;
}

/// <summary>A maximal continuous run of sections between explicit boundaries.</summary>
public sealed record Segment
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> SectionIds { get; init; }
    public required double TopM { get; init; }
    public required double BaseM { get; init; }
    public required string DepthDatum { get; init; }
    public BoundaryKind BoundaryAbove { get; init; } = BoundaryKind.None;
    public BoundaryKind BoundaryBelow { get; init; } = BoundaryKind.None;
}

/// <summary>Sedimentation-rate interval between two consecutive anchors.</summary>
public sealed record RateInterval(
    string FromAnchorId, string ToAnchorId,
    double DepthLoM, double DepthHiM,
    double RateLo, double RateMid, double RateHi, string Unit);

/// <summary>A defensible candidate depth-age model. Immutable once derived.</summary>
public sealed record ModelCandidate
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    /// <summary>Anchor ids in depth order; ties resolved by this candidate's ordering.</summary>
    public required IReadOnlyList<string> AnchorOrder { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    /// <summary>Uncertainty band: (depthM, ageLoKa, ageMidKa, ageHiKa) polyline points.</summary>
    public required IReadOnlyList<(double DepthM, double Lo, double Mid, double Hi)> Band { get; init; }
    public required string Status { get; init; } // "admissible" | "rejected"
    public string? RejectReason { get; init; }
    /// <summary>Digest of the exact inputs used.</summary>
    public required string InputDigest { get; init; }
    /// <summary>Rule/parameter version used for the derivation.</summary>
    public required string RuleVersion { get; init; }
    /// <summary>Parent dependencies: entity ids and event seqs this derives from.</summary>
    public required IReadOnlyList<string> Parents { get; init; }
    public string? FrozenBy { get; init; }
    public string? FrozenAt { get; init; }
}
