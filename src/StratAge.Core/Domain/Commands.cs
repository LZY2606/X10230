namespace StratAge.Core.Domain;

// 命令记录（= 事件有效载荷）。材料类命令是不可变事实；版本化命令通过 branchId/version 支持局部重算。

public sealed record DistributionSpec
{
    public required string Kind { get; init; } // normal | uniform | empirical | radiocarbon
    public double? Mean { get; init; }
    public double? Sigma { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<double>? Samples { get; init; }
    public string Unit { get; init; } = "";
    public string? CalibrationVersion { get; init; }
    public double? ReservoirDeltaR { get; init; }
    public string? ReservoirId { get; init; }
}

public abstract record Command
{
    public required string Id { get; init; }
    public string? BranchId { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public string? Note { get; init; }
    public abstract string Type { get; }
}

public sealed record CreateStudy(string StudyId, string Name, string DatumUnit, string DatumBasis, string TimeBasis)
{
    public string Type => "study.created";
}

public sealed record DefineSection(
    string Id, string TopRaw, string BottomRaw, string Kind, string? BranchId = null,
    int Version = 1, string? ParentId = null)
{
    public string Type => "section.defined";
}

public sealed record PutAnchor(
    string Id, string SectionId, string DepthRaw, DistributionSpec AgeDist,
    string Material = "generic", string? PairKey = null, string? EventId = null,
    string? BranchId = null, int Version = 1, string? ParentId = null)
{
    public string Type => "anchor.put";
}

public sealed record DefineReservoir(string Id, string Name, DistributionSpec DeltaR)
{
    public string Type => "reservoir.defined";
}

public sealed record PutEvent(string Id, string Name, string Kind, string DepthRaw, string? CorrelatesTo = null)
{
    public string Type => "event.put";
}

public sealed record RecordExclusion(string Id, string TargetId, string TargetKind, string Reason, string Basis)
{
    public string Type => "exclusion.recorded";
}

public sealed record HumanDecision(string Id, string Kind, string Scope, string Value, string ConflictsWith,
    string? BranchId = null)
{
    public string Type => "decision.human";
}

public sealed record ReviseParameter(string Id, string Name, string NewVersion, string OldVersion, string Note)
{
    public string Type => "parameter.revised";
}

public sealed record FreezeModel(string Id, string ModelRunId)
{
    public string Type => "model.frozen";
}

public sealed record TryAnchor(
    string Id, string FrozenRunId, string SectionId, string DepthRaw, DistributionSpec AgeDist,
    string Material = "generic", string? EventId = null)
{
    public string Type => "anchor.tried";
}

public sealed record ResolveTrial(string Id, string TrialId, string Outcome, string? AcceptedRunId = null)
{
    public string Type => "trial.resolved";
}

public sealed record RecordCounterExperiment(
    string Id, string BaselineMarker, string ChangedSourceId, string ChangeKind, string ChangeJson,
    string RuleCode, string ExpectedContainment, bool Adopted, string Outcome, string AffectedIdsJson,
    IReadOnlyList<CounterFinding> Findings)
{
    public string Type => "counter.recorded";
}

public sealed record CounterFinding
{
    public required string RuleCode { get; init; }
    public required string Status { get; init; } // violated | contained | inconclusive
    public required string ObjectId { get; init; }
    public string Detail { get; init; } = "";
}
