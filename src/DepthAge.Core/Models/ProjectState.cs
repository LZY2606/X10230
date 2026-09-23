using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepthAge.Core.Models;

/// <summary>事件重放得到的聚合状态；回到任意旧事件即得到当时的状态。</summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Seq { get; set; }
    public string LastHash { get; set; } = ProjectEvent.GenesisHash;
    public string BranchId { get; set; } = "main";
    public RuleBook Rules { get; set; } = RuleBook.Initial();

    public Dictionary<string, DepthDatum> Datums { get; } = new();
    public List<CoreSection> Sections { get; } = new();
    public Dictionary<string, AnchorMaterial> Anchors { get; } = new();
    public Dictionary<string, ReservoirOffset> ReservoirOffsets { get; } = new();
    public Dictionary<string, CalibrationCurve> CalibrationCurves { get; } = new();
    public Dictionary<string, EventBed> EventBeds { get; } = new();
    public List<EventCorrelation> EventCorrelations { get; } = new();
    public List<ExclusionDecision> Exclusions { get; } = new();
    public List<ComputationRecord> Computations { get; } = new();
    public List<FreezeRecord> Freezes { get; } = new();
    public List<DecisionRecord> Decisions { get; } = new();
    public List<Judgment> Judgments { get; } = new();
    public List<Diagnostic> ImportDiagnostics { get; } = new();

    public IEnumerable<AnchorMaterial> ActiveAnchors =>
        Anchors.Values.Where(a => !a.Excluded).OrderBy(a => a.Depth.Quantity.RequireCanonical());
}

public sealed record DecisionRecord
{
    public required string Id { get; init; }
    public required string Kind { get; init; } // Accept | Reject | Adjust
    public required string CandidateId { get; init; }
    public required string ComputationId { get; init; }
    public required long AtSeq { get; init; }
    public required string RuleVersion { get; init; }
    public string? Note { get; init; }
    public Dictionary<string, double> AdjustedParameters { get; init; } = new();
}

public sealed record FreezeRecord
{
    public required string Id { get; init; }
    public required long AtSeq { get; init; }
    public required string RuleVersion { get; init; }
    public required string ComputationId { get; init; }
    public required string FrozenCandidateId { get; init; }
    public required string InputDigest { get; init; }
    public required DateTimeOffset FrozenAt { get; init; }
    public bool Superseded { get; init; }
    public string? SupersedeReason { get; init; }
}
