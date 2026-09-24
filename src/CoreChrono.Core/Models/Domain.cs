using System.Text.Json.Serialization;

namespace CoreChrono.Core.Models;

public enum Severity { Error, Warning, Info }

/// <summary>A located diagnostic. Position is a stable material-relative locator, never a UI row.</summary>
public sealed record Diagnostic(string Code, Severity Severity, string Message, string Position)
{
    public override string ToString() => $"[{Severity}] {Code} @ {Position}: {Message}";
}

/// <summary>Depth datum / reference frame for a core. Immutable once imported.</summary>
public sealed record DepthDatum
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";          // e.g. "core-top", "MSL"
    public string Direction { get; init; } = "down"; // down = depth increases downward
    public string CoordinateReference { get; init; } = ""; // e.g. "metres below core top"
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>
/// A stratigraphic segment. Bounds are depths in the core frame. A segment may be
/// continuous or carry an explicit gap/unconformity at its base boundary. Monotonic
/// age constraints only ever hold inside a continuous segment.
/// </summary>
public sealed record Segment
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public Quantity TopDepth { get; init; } = Quantity.Parse("", "m");
    public Quantity BaseDepth { get; init; } = Quantity.Parse("", "m");

    /// <summary>continuous | gap | unconformity | fault — only 'continuous' allows interpolation across.</summary>
    public string Kind { get; init; } = "continuous";
    public Quantity? GapThickness { get; init; }      // missing section thickness if gap/unconformity
    public Quantity? MissingAge { get; init; }        // estimated missing time, optional
    public string BoundaryNote { get; init; } = "";
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>
/// Radiometric age attached to a depth. The whole distribution is first-class:
/// samples or quantiles survive; a mean must never replace it.
/// </summary>
public sealed record AgeAnchor
{
    public string Id { get; init; } = "";
    public string SegmentId { get; init; } = "";
    public string Material { get; init; } = "";      // planktic foraminifera, wood, bulk organic ...
    public Quantity Depth { get; init; } = Quantity.Parse("", "m");
    public string DistributionKind { get; init; } = "normal"; // normal|samples|uniform|lognormal
    public double? MeanAge { get; init; }            // canonical years (informational only)
    public double? StdAge { get; init; }             // canonical years
    public double? MinAge { get; init; }
    public double? MaxAge { get; init; }
    public List<double> Samples { get; init; } = new(); // canonical years, long tail lives here
    public string AgeScale { get; init; } = "BP";    // BP|AD|...
    public string ReservoirOffsetId { get; init; } = "";
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>Reservoir / hard-water offset applied (or not) to a dated sample. Immutable source.</summary>
public sealed record ReservoirOffset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Quantity DeltaR { get; init; } = Quantity.Parse("", "yr");
    public double UncertaintyYears { get; init; }
    public string AppliesToMaterial { get; init; } = "";
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>Event bed (tephra, flood, biostrat event) used for correlation between anchors/cores.</summary>
public sealed record EventBed
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public Quantity Depth { get; init; } = Quantity.Parse("", "m");
    public Quantity? EventAge { get; init; }
    public double? EventAgeStd { get; init; }
    public string CorrelationKey { get; init; } = ""; // tephra geochem fingerprint / zone code
    public double CorrelationConfidence { get; init; } = 1.0;
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>Every exclusion (rejected date, rejected correlation) is retained with reason.</summary>
public sealed record ExclusionDecision
{
    public string Id { get; init; } = "";
    public string TargetKind { get; init; } = "";    // anchor|event|segment
    public string TargetId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Basis { get; init; } = "";         // rule id or human decision
    public bool Excluded { get; init; } = true;
    public Dictionary<string, string> Provenance { get; init; } = new();
}

/// <summary>
/// The immutable imported material bundle. Imported raw representations are kept verbatim;
/// only derived calculations normalize. This is the single direct source of truth for a case.
/// </summary>
public sealed record SourceBundle
{
    public string CaseId { get; init; } = "";
    public string SchemaVersion { get; init; } = "1.0";
    public DepthDatum Datum { get; init; } = new();
    public List<Segment> Segments { get; init; } = new();
    public List<AgeAnchor> Anchors { get; init; } = new();
    public List<ReservoirOffset> ReservoirOffsets { get; init; } = new();
    public List<EventBed> Events { get; init; } = new();
    public List<ExclusionDecision> Exclusions { get; init; } = new();
    public Dictionary<string, string> Meta { get; init; } = new();
}
