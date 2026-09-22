using System.Text.Json.Serialization;

namespace Chrono.Core.Case;

/// <summary>
/// The offline, domain-specific case package "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// It is embedded content (never a generic spreadsheet) and carries the full narrative: datum,
/// gaps, radiometric distributions, reservoir offsets, event correlations and retained exclusions.
/// </summary>
public sealed class CasePackage
{
    public string Schema { get; set; } = "chrono-case/1";
    public string CaseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string RuleVersion { get; set; } = "rules-2026.09";
    public string Datum { get; set; } = "";
    public string DatumFrame { get; set; } = "";
    public List<CaseSegment> Segments { get; set; } = new();
    public List<CaseAnchor> Anchors { get; set; } = new();
    public List<CaseReservoir> Reservoirs { get; set; } = new();
    public List<CaseEvent> Events { get; set; } = new();
    public List<CaseExclusion> Exclusions { get; set; } = new();
    /// <summary>Scripted replay steps: parameter revision, local-only source edit, conflicting decisions.</summary>
    public List<CaseStep> Replay { get; set; } = new();
}

public sealed class CaseSegment
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Top { get; set; } = "";
    public string TopUnit { get; set; } = "m";
    public string Bottom { get; set; } = "";
    public string BottomUnit { get; set; } = "m";
    /// <summary>conformable | unconformity | recovery-gap</summary>
    public string LowerContact { get; set; } = "conformable";
    public string? ContactRaw { get; set; }
    public string Lithology { get; set; } = "";
}

public sealed class CaseAnchor
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Depth { get; set; } = "";
    public string DepthUnit { get; set; } = "m";
    /// <summary>Preserved raw representation of the radiometric distribution.</summary>
    public string Age { get; set; } = "";
    public string? ReservoirId { get; set; }
    public string Material { get; set; } = "";
}

public sealed class CaseReservoir
{
    public string Id { get; set; } = "";
    public double MeanKa { get; set; }
    public double SigmaKa { get; set; }
    public string ReferenceCurve { get; set; } = "";
    public string Raw { get; set; } = "";
}

public sealed class CaseEvent
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Depth { get; set; } = "";
    public string DepthUnit { get; set; } = "m";
    public string Correlation { get; set; } = "";
    public string ExternalAgeReference { get; set; } = "";
}

public sealed class CaseExclusion
{
    public string Id { get; set; } = "";
    public string AnchorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string DecidedBy { get; set; } = "";
}

/// <summary>
/// One scripted narrative step. <see cref="Kind"/> maps to an event type; <see cref="Expect"/> lists
/// situations the replay must actually pass through (duplicate, unconformity, inversion, tie,
/// long-tail, local-recompute, concurrent-freeze).
/// </summary>
public sealed class CaseStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string? FromStep { get; set; }
    public Dictionary<string, object?> Payload { get; set; } = new();
    public List<string> Expect { get; set; } = new();
}
