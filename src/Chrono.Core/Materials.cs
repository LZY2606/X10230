using System.Text.Json.Serialization;

namespace Chrono.Core;

public enum MaterialKind
{
    DepthDatum, Segment, AgeAnchor, ReservoirOffset, EventCorrelation, ExclusionDecision
}

public enum AgeSampleType { Calibrated14C, OSL, Tephra, Pb210, UTh, Unsupported }

/// <summary>
/// An age *distribution*, never collapsed to a mean. Empirical samples are kept ordered and used
/// directly for convolution/uncertainty bands; parametrics are sampled with a deterministic seed.
/// </summary>
public sealed record AgeDistribution
{
    public string Id { get; init; } = "";
    public AgeSampleType SampleType { get; init; }
    public string DistributionKind { get; init; } = "empirical"; // empirical | normal | skew-normal
    public double[] SamplesCalBP { get; init; } = System.Array.Empty<double>();
    public double? MuCalBP { get; init; }
    public double? SigmaCalBP { get; init; }
    public double? Skew { get; init; }
    public int? LongTailPpm { get; init; }
    public string? TailNote { get; init; }

    public double MedianCalBP() => Quantile(0.5);

    public double Quantile(double p)
    {
        var s = EffectiveSamples();
        if (s.Length == 0) return double.NaN;
        var idx = Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1);
        return s[idx];
    }

    public double[] EffectiveSamples(int? budget = null)
    {
        if (DistributionKind == "empirical" || SamplesCalBP.Length > 0)
            return budget is int b && b < SamplesCalBP.Length ? SamplesCalBP[..b] : SamplesCalBP;
        // Parametric distributions are sampled deterministically by the engine which owns the seed.
        return SamplesCalBP;
    }
}

/// <summary>Depth reference frame. Depth increases downward; datum identifies zero.</summary>
public sealed record DepthDatum
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double ZeroMbsf { get; init; }
    public string DepthDirection { get; init; } = "down-positive";
    public string VerticalDatum { get; init; } = "seafloor";
}

public enum SegmentRelation { Continuous, CoreGap, Unconformity }

/// <summary>A litho/chronostrat segment. Gaps and unconformities are explicit boundaries.</summary>
public sealed record Segment
{
    public string Id { get; init; } = "";
    public string Lithology { get; init; } = "";
    public double TopDepthM { get; init; }
    public double BaseDepthM { get; init; }
    public SegmentRelation BaseRelation { get; init; } = SegmentRelation.Continuous;
    public double? MissingBaseKa { get; init; }
    public double? MissingTopKa { get; init; }
    public string? Note { get; init; }
}

/// <summary>An age control point. Depth keeps the original text/unit; age keeps its full distribution.</summary>
public sealed record AgeAnchor
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public RawQuantity Depth { get; init; } = new();
    public AgeDistribution Distribution { get; init; } = new();
    public string? ReservoirOffsetId { get; init; }
    public string SourceRef { get; init; } = "";
    public bool Excluded { get; init; }
    public string? ExcludeReason { get; init; }
    public string EventId { get; init; } = ""; // event that introduced/excluded this anchor (lineage)
}

public sealed record ReservoirOffset
{
    public string Id { get; init; } = "";
    public RawQuantity DeltaR { get; init; } = new();       // local reservoir correction, yr
    public RawQuantity SigmaR { get; init; } = new();
    public string AppliesToSampleType { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed record EventCorrelation
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public RawQuantity Depth { get; init; } = new();
    public RawQuantity? AgeCalBP { get; init; }
    public string CorrelationBasis { get; init; } = "";   // tephra geochem, isotope, biostrat...
    public string ExternalRef { get; init; } = "";
}

public sealed record ExclusionDecision
{
    public string TargetMaterialId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string DecidedByEvent { get; init; } = "";
    public bool LaterRevoked { get; init; }
}

/// <summary>Diagnostic attached to a specific material/field with a JSON-pointer-ish location.</summary>
public sealed record ImportDiagnostic(
    string Severity, string Code, string Location, string Message,
    IReadOnlyList<string> AffectedIds)
{
    public ImportDiagnostic(string severity, string code, string location, string message)
        : this(severity, code, location, message, Array.Empty<string>()) { }
}

/// <summary>
/// Immutable material layer. Imported once; replacements create a new revision with a new event id.
/// </summary>
public sealed record MaterialSet
{
    public string RevisionId { get; init; } = "";
    public DepthDatum Datum { get; init; } = new();
    public IReadOnlyList<Segment> Segments { get; init; } = System.Array.Empty<Segment>();
    public IReadOnlyList<AgeAnchor> Anchors { get; init; } = System.Array.Empty<AgeAnchor>();
    public IReadOnlyList<ReservoirOffset> ReservoirOffsets { get; init; } = System.Array.Empty<ReservoirOffset>();
    public IReadOnlyList<EventCorrelation> Events { get; init; } = System.Array.Empty<EventCorrelation>();
    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = System.Array.Empty<ImportDiagnostic>();
    public IReadOnlyList<ExclusionDecision> Exclusions { get; init; } = System.Array.Empty<ExclusionDecision>();

    public IEnumerable<AgeAnchor> ActiveAnchors =>
        Anchors.Where(a => !a.Excluded && a.Depth.IsConverted);
}
