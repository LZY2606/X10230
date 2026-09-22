using System.Text.Json.Serialization;

namespace ChronoCore;

/// <summary>
/// A radiometric age is a full distribution, never a point. Raw representation is preserved;
/// the quantile summary is a derived normalization.
/// </summary>
public sealed class AgeDistribution
{
    public string Kind { get; set; } = "normal";        // normal | lognormal | gamma | empirical
    public double? MeanYearsBp { get; set; }
    public double? StdYearsBp { get; set; }
    public double? Shape { get; set; }                  // gamma k or lognormal sigma
    public double? Scale { get; set; }                  // gamma theta
    public double? OffsetYearsBp { get; set; }          // lognormal shift / empirical shift
    public List<double> EmpiricalSamples { get; set; } = new();
    [JsonIgnore] public bool IsLongTail => Kind is "lognormal" or "gamma";
}

/// <summary>Reservoir offset (e.g. marine ΔR) applied to a radiocarbon anchor. Immutable evidence.</summary>
public sealed class ReservoirOffset
{
    public double MeanYears { get; set; }
    public double StdYears { get; set; }
    public string Basis { get; set; } = "";             // e.g. "Marine20 ΔR local calibration"
}

/// <summary>A depth/age anchor. Distribution is retained; the mean never replaces it.</summary>
public sealed class Anchor
{
    public string Id { get; set; } = "";                // stable, e.g. AN-001
    public string Method { get; set; } = "";            // Ar-Ar | U-Pb | radiocarbon | tephra-match | ...
    public DepthPoint Depth { get; set; } = null!;
    public AgeQuantity RawAge { get; set; } = null!;    // raw lab age representation retained
    public AgeDistribution Distribution { get; set; } = new();
    public ReservoirOffset Reservoir { get; set; } = new();
    public string EventLayerId { get; set; } = "";      // correlation evidence, if any
    public string LabCode { get; set; } = "";
    public string Notes { get; set; } = "";
    public int Revision { get; set; } = 1;
    public string SourceRef { get; set; } = "";         // which material/row produced it
}

public static class BoundaryKind
{
    public const string Continuous = "continuous";
    public const string Gap = "gap";                    // 分段缺口
    public const string Unconformity = "unconformity";  // 不整合
}

public sealed class CoreSegment
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DepthPoint Top { get; set; } = null!;
    public DepthPoint Bottom { get; set; } = null!;
    public string UpperBoundary { get; set; } = BoundaryKind.Continuous;
    public string LowerBoundary { get; set; } = BoundaryKind.Continuous;
    public double? GapYearsBp { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class EventLayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DepthPoint Depth { get; set; } = null!;
    public AgeDistribution ReferenceDistribution { get; set; } = new();
    public string CorrelationBasis { get; set; } = "";  // 事件层相关依据
    public double CorrelationConfidence { get; set; } = 1.0;
}

/// <summary>An exclusion is a mutable decision; its history is kept in the event log.</summary>
public sealed class ExclusionDecision
{
    public string Id { get; set; } = "";                // e.g. EX-001
    public string SubjectType { get; set; } = "";       // anchor | segment | event
    public string SubjectId { get; set; } = "";
    public bool Excluded { get; set; }
    public string Reason { get; set; } = "";
    public string DecidedBy { get; set; } = "";
}

public sealed record Diagnostic
{
    public string Code { get; init; } = "";             // INCOMPLETE_INPUT | FORMAT_CORRUPT | SEMANTIC_UNRESOLVABLE | ...
    public string Location { get; init; } = "";         // JSON pointer / stable id
    public string Message { get; init; } = "";
    public string Severity { get; init; } = "error";
}

/// <summary>
/// Immutable evidence bundle. Material is never overwritten: a source change creates a new
/// revision on the affected item. The current view selects the highest revision per id.
/// </summary>
public sealed class MaterialsDoc
{
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string DepthDatum { get; set; } = "mbsf@seafloor";
    public string TimeBasis { get; set; } = "a BP (before 1950 CE)";
    public List<Anchor> Anchors { get; set; } = new();
    public List<CoreSegment> Segments { get; set; } = new();
    public List<EventLayer> Events { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
}
