using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

// ---------------------------------------------------------------------------
// IMMUTABLE MATERIAL LAYER
// Core segmentation, age anchors, radiometric distributions, reservoir
// offsets and event beds are imported evidence. They are never mutated;
// decisions (exclusions, parameter revisions, acceptance) live separately.
// ---------------------------------------------------------------------------

public enum BoundaryKind { Conformable, Gap, Unconformity, Fault }

/// <summary>A core segment bounded by explicit stratigraphic boundaries.
/// Monotonic age ordering is only asserted inside a non-faulted segment.</summary>
public sealed record Segment
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Measure TopDepth { get; init; }
    public required Measure BottomDepth { get; init; }
    public BoundaryKind UpperBoundary { get; init; } = BoundaryKind.Conformable;
    public BoundaryKind LowerBoundary { get; init; } = BoundaryKind.Conformable;
    public string? BoundaryNote { get; init; }
    public int? Ordering { get; init; }
}

/// <summary>Shape of a radiometric age distribution. A distribution is never
/// replaced by its mean; quantiles and tails drive the uncertainty band.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalAge), "normal")]
[JsonDerivedType(typeof(LognormalAge), "lognormal")]
[JsonDerivedType(typeof(StudentTAge), "studentT")]
[JsonDerivedType(typeof(EmpiricalAge), "empirical")]
public abstract record AgeDistribution
{
    public abstract string Describe();
    public abstract double Quantile(double p);
}

public sealed record NormalAge(double MeanKa, double SigmaKa) : AgeDistribution
{
    public override string Describe() => $"N({MeanKa:G},σ={SigmaKa:G} ka)";
    public override double Quantile(double p)
        => MeanKa + SigmaKa * MathNetLite.Probit(p);
}

/// <summary>Long-tail distribution: lognormal, used for the "age tail" case.</summary>
public sealed record LognormalAge(double MedianKa, double SigmaLog) : AgeDistribution
{
    public override string Describe() => $"LogN(med={MedianKa:G},σln={SigmaLog:G})";
    public override double Quantile(double p)
        => MedianKa * Math.Exp(SigmaLog * MathNetLite.Probit(p));
}

/// <summary>Heavy-tail Student-t used for reservoir-corrected radiocarbon.</summary>
public sealed record StudentTAge(double LocationKa, double ScaleKa, double Df) : AgeDistribution
{
    public override string Describe() => $"t(μ={LocationKa:G},s={ScaleKa:G},df={Df:G})";
    public override double Quantile(double p)
        => LocationKa + ScaleKa * MathNetLite.TInv(p, Df);
}

/// <summary>Empirical samples (e.g. a published posterior). Preserved verbatim.</summary>
public sealed record EmpiricalAge(List<double> SamplesKa) : AgeDistribution
{
    public override string Describe() => $"empirical(n={SamplesKa.Count})";
    public override double Quantile(double p)
    {
        var s = new List<double>(SamplesKa);
        s.Sort();
        if (s.Count == 0) return double.NaN;
        var idx = Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1);
        return s[idx];
    }
}

/// <summary>Reservoir offset (e.g. marine radiocarbon reservoir) applied to an anchor.
/// It is a separate evidence item, never silently folded into the measured age.</summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string AnchorId { get; init; }
    public required Measure Offset { get; init; }      // positive = subtract from apparent age
    public required string Method { get; init; }       // e.g. "marine13", "local ΔR"
    public required string TimeBasis { get; init; }    // "BP1950"
}

/// <summary>A radiometric age anchor at depth. Holds the full distribution.</summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Measure Depth { get; init; }
    public string? SegmentId { get; init; }
    public required AgeDistribution Distribution { get; init; }
    public string Material { get; init; } = "";
    public string DatingMethod { get; init; } = "";
    public string? ReservoirOffsetId { get; init; }
    public bool TieCandidate { get; init; }             // may be tied to an event bed
    public string? EventBedId { get; init; }
    public string SourceRef { get; init; } = "";        // provenance citation / source fragment
}

/// <summary>An event (tephra, extinction, isotope excursion) correlatable to a datum age.</summary>
public sealed record EventBed
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public Measure? Depth { get; init; }
    public AgeDistribution? DatumAge { get; init; }     // global reference age, if known
    public string Correlation { get; init; } = "";      // evidence text for the correlation
    public required string SourceRef { get; init; }
}

// ---------------------------------------------------------------------------
// MUTABLE DECISION LAYER
// Exclusions and their rescission are decisions; they reference immutable items.
// ---------------------------------------------------------------------------

public enum ExclusionTarget { Anchor, EventBed, ReservoirOffset, Segment }
public enum DecisionStatus { Proposed, Accepted, Rejected, Rescinded, Superseded }

public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required ExclusionTarget TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required string Reason { get; init; }
    public required string SourceRef { get; init; }
    public DecisionStatus Status { get; init; } = DecisionStatus.Accepted;
    public string? SupersededByEventId { get; init; }
}

/// <summary>Versioned derivation parameter pack. Each revision gets a new version.</summary>
public sealed record ParameterSet
{
    public required string Version { get; init; }
    public string Interpolation { get; init; } = "linear"; // linear | pchip
    public bool EnforceWithinSegmentMonotonicity { get; init; } = true;
    public double ExtrapolateSegmentM { get; init; } = 0.0; // 0 => no extrapolation across gaps
    public int MonteCarloDraws { get; init; } = 2000;
    public ulong RandomSeed { get; init; } = 20260923UL;
    public int CandidateBudget { get; init; } = 256;       // resource budget for enumeration
    public string? Note { get; init; }
}

/// <summary>A snapshot of imported inputs for a project (the "baseline" evidence bundle).</summary>
public sealed record EvidenceBundle
{
    public required string SchemaVersion { get; set; }
    public DepthDatum? Datum { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public List<AgeAnchor> Anchors { get; set; } = new();
    public List<ReservoirOffset> ReservoirOffsets { get; set; } = new();
    public List<EventBed> EventBeds { get; set; } = new();
    public List<ExclusionDecision> Exclusions { get; set; } = new();
    public ParameterSet Parameters { get; set; } = new() { Version = "P1" };
    /// <summary>Import-time problems. Unconvertible values stay in place and appear here.</summary>
    public List<ImportDiagnostic> Diagnostics { get; set; } = new();
    public List<string> InputFragments { get; set; } = new();
}

public sealed record ImportDiagnostic(string Code, string Path, string Message, string Severity)
{
    public override string ToString() => $"[{Severity}] {Path}: {Message} ({Code})";
}
