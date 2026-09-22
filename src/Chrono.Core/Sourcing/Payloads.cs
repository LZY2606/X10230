using System.Text.Json.Serialization;

namespace Chrono.Core.Sourcing;

public sealed class BaselineIngestPayload
{
    public string CaseId { get; set; } = "";
    public string CaseVersion { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string DatumId { get; set; } = "";
    public string DatumRaw { get; set; } = "";
    public string DatumFrame { get; set; } = "";
    public double DatumTopMetres { get; set; }
    public List<SegmentSnapshot> Segments { get; set; } = new();
    public List<AnchorSnapshot> Anchors { get; set; } = new();
    public List<EventSnapshot> Events { get; set; } = new();
    public List<ReservoirSnapshot> Reservoirs { get; set; } = new();
    public List<ExclusionSnapshot> Exclusions { get; set; } = new();
}

public sealed class SegmentSnapshot
{
    public string Id { get; set; } = ""; public string Code { get; set; } = "";
    public string RawTop { get; set; } = ""; public string RawTopUnit { get; set; } = "m";
    public string RawBottom { get; set; } = ""; public string RawBottomUnit { get; set; } = "m";
    public double TopMetres { get; set; } public double BottomMetres { get; set; }
    public string LowerContact { get; set; } = "conformable"; public string? ContactRaw { get; set; }
    public string Lithology { get; set; } = "";
}

public sealed class AnchorSnapshot
{
    public string Id { get; set; } = ""; public string Label { get; set; } = "";
    public string RawDepth { get; set; } = ""; public string RawDepthUnit { get; set; } = "m";
    public double? DepthMetres { get; set; }
    public string RawAge { get; set; } = ""; public string? AgeKind { get; set; }
    public string? ReservoirOffsetId { get; set; } public string Material { get; set; } = "";
    public string? UnconvertibleReason { get; set; }
}

public sealed class EventSnapshot
{
    public string Id { get; set; } = ""; public string Code { get; set; } = "";
    public string RawDepth { get; set; } = ""; public string RawDepthUnit { get; set; } = "m";
    public double? DepthMetres { get; set; }
    public string Correlation { get; set; } = ""; public string ExternalAgeReference { get; set; } = "";
}

public sealed class ReservoirSnapshot
{
    public string Id { get; set; } = ""; public double MeanKa { get; set; }
    public double SigmaKa { get; set; } public string ReferenceCurve { get; set; } = "";
    public string RawText { get; set; } = "";
}

public sealed class ExclusionSnapshot
{
    public string Id { get; set; } = ""; public string AnchorId { get; set; } = "";
    public string Reason { get; set; } = ""; public string DecidedBy { get; set; } = "";
}

public sealed class MaterialAmendPayload
{
    /// <summary>segment | anchor | event | reservoir</summary>
    public string Kind { get; set; } = "";
    public string MaterialId { get; set; } = "";
    /// <summary>Human label of the single direct source being changed.</summary>
    public string DirectSource { get; set; } = "";
    public Dictionary<string, string?> Fields { get; set; } = new();
}

public sealed class ExclusionPayload
{
    public string AnchorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Active { get; set; } = true;
    public string DecidedBy { get; set; } = "";
}

public sealed class DuplicateResolvePayload
{
    public double DepthMetres { get; set; }
    public List<string> AnchorIds { get; set; } = new();
    public string Mode { get; set; } = "alternate";
    public string? DroppedAnchorId { get; set; }
}

public sealed class InversionBoundaryPayload
{
    public double AtDepthMetres { get; set; }
    public string Contact { get; set; } = "unconformity";
    public string Justification { get; set; } = "";
}

public sealed class ParametersPayload
{
    public int? Samples { get; set; }
    public int? DepthGridPoints { get; set; }
    public long? BudgetDraws { get; set; }
    public List<string>? Strategies { get; set; }
    public bool? ApplyReservoir { get; set; }
}

public sealed class CandidateDecisionPayload
{
    public string RunId { get; set; } = "";
    public List<string> AcceptedCandidateIds { get; set; } = new();
    public string Decision { get; set; } = "accept";
}

public sealed class FreezePayload
{
    public string RunId { get; set; } = "";
    public List<string> CandidateIds { get; set; } = new();
    public string CompareToken { get; set; } = "";
}

public sealed class CompareAnchorPayload
{
    public string Label { get; set; } = "";
    public double DepthMetres { get; set; }
    public string RawAge { get; set; } = "";
}

public sealed class FindingPayload
{
    public string Code { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool Holds { get; set; }
    public string Justification { get; set; } = "";
    public List<string> AffectedObjectIds { get; set; } = new();
}

public sealed class ProjectCreatedPayload
{
    public string Name { get; set; } = "";
    public string CaseId { get; set; } = "";
    public string CaseVersion { get; set; } = "";
}
