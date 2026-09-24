namespace AgeModel.Domain;

/// <summary>A quantity that keeps its raw textual representation, unit and datum.
/// Normalization happens only in the derived layer; unconvertible values stay in place.</summary>
public sealed record Quantity(string Raw, string Unit, string Datum)
{
    public double? Value { get; init; }
    public string? Diagnostic { get; init; }

    public static Quantity Parse(string raw, string unit, string datum)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return new Quantity(raw, unit, datum) { Value = v };
        return new Quantity(raw, unit, datum)
        {
            Value = null,
            Diagnostic = $"unconvertible value '{raw}' kept in place with unit '{unit}'"
        };
    }
}

/// <summary>One quantile of an age distribution, e.g. P=0.5, Age=12.4 (ka BP).</summary>
public sealed record QuantilePoint(double P, double Age);

/// <summary>A radiometric age distribution. Never collapsed to a mean:
/// the full quantile set is the source of truth for every downstream computation.</summary>
public sealed record AgeDistribution(IReadOnlyList<QuantilePoint> Quantiles, string Unit)
{
    public double At(double p)
    {
        var q = Quantiles;
        if (q.Count == 0) throw new InvalidOperationException("empty distribution");
        if (p <= q[0].P) return q[0].Age;
        if (p >= q[^1].P) return q[^1].Age;
        for (var i = 1; i < q.Count; i++)
        {
            if (p <= q[i].P)
            {
                var t = (p - q[i - 1].P) / (q[i].P - q[i - 1].P);
                return q[i - 1].Age + t * (q[i].Age - q[i - 1].Age);
            }
        }
        return q[^1].Age;
    }

    public double Median => At(0.5);
    public double Lower => At(0.16);
    public double Upper => At(0.84);

    /// <summary>Skew of the quantile set; non-zero for long-tail distributions.</summary>
    public double Skew => (Upper - Median) - (Median - Lower);
}

public static class MaterialKind
{
    public const string DepthDatum = "depth-datum";
    public const string SegmentGap = "segment-gap";
    public const string Unconformity = "unconformity";
    public const string RadiometricAge = "radiometric-age";
    public const string ReservoirOffset = "reservoir-offset";
    public const string EventLayer = "event-layer";
    public const string ExclusionDecision = "exclusion-decision";
}

/// <summary>Immutable material. Materials are never edited in place;
/// corrections arrive as new events that supersede them.</summary>
public abstract record Material(string Id, string Kind, int ImportSeq);

public sealed record DepthDatumMaterial(string Id, int ImportSeq, string DatumName, string ReferenceFrame)
    : Material(Id, MaterialKind.DepthDatum, ImportSeq);

/// <summary>An explicit boundary: a core gap or an unconformity.
/// Age may jump across it; monotonicity is only required inside a continuous segment.</summary>
public sealed record BoundaryMaterial(string Id, int ImportSeq, string BoundaryKind, Quantity Depth, string Reason)
    : Material(Id, BoundaryKind, ImportSeq);

public sealed record RadiometricAgeMaterial(
    string Id, int ImportSeq, Quantity Depth, AgeDistribution Age,
    string LabCode, Quantity? ReservoirOffsetYears) : Material(Id, MaterialKind.RadiometricAge, ImportSeq)
{
    /// <summary>Age distribution corrected by the reservoir offset, still a full distribution.</summary>
    public AgeDistribution CorrectedAge()
    {
        var offset = ReservoirOffsetYears?.Value ?? 0.0;
        if (offset == 0.0) return Age;
        var ka = offset / 1000.0;
        return new AgeDistribution(
            Age.Quantiles.Select(q => q with { Age = q.Age - ka }).ToList(), Age.Unit);
    }
}

public sealed record EventLayerMaterial(string Id, int ImportSeq, Quantity Depth, string Name, string CorrelatedTo)
    : Material(Id, MaterialKind.EventLayer, ImportSeq);

public sealed record ExclusionDecisionMaterial(string Id, int ImportSeq, string TargetMaterialId, string Reason, string DecidedBy)
    : Material(Id, MaterialKind.ExclusionDecision, ImportSeq);

/// <summary>A machine-readable judgement emitted by the engine.
/// Every rule the brief names (monotonic-only-in-continuous-segment, explicit boundaries,
/// distribution-not-mean, keep-equal-candidates) maps to one of these codes.</summary>
public sealed record Judgement(string Code, string? SegmentId, double? Depth, string Detail)
{
    public const string MonotonicEnforced = "monotonic-within-continuous-segment";
    public const string ExplicitBoundary = "explicit-boundary";
    public const string DistributionKept = "distribution-not-replaced-by-mean";
    public const string EqualCandidatesKept = "equal-candidates-retained";
    public const string DepthDuplicate = "depth-duplicate";
    public const string ParallelAnchors = "parallel-anchors";
    public const string InvertedSection = "inverted-section";
    public const string LongTailDistribution = "long-tail-distribution";
    public const string LocalRecompute = "local-recompute";
    public const string UnconvertibleValue = "unconvertible-value-kept";
}

public sealed record Parameters(
    int MaxCombinations = 256,
    double EqualFitToleranceKa = 0.05,
    double LongTailSkewKa = 0.5,
    int Version = 1);
