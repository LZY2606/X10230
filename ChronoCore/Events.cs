namespace ChronoCore;
using System.Text.Json;

public static class EventType
{
    public const string ProjectCreated = "project.created";
    public const string MaterialsImported = "materials.imported";
    public const string SourceChanged = "source.changed";        // 直接来源改动（新修订版本）
    public const string ParameterRevised = "parameter.revised";
    public const string ModelBuilt = "model.built";
    public const string JudgmentRecorded = "judgment.recorded";  // 机器可读判定
    public const string DecisionExclude = "decision.exclude";
    public const string DecisionPolicy = "decision.policy";      // 互相冲突的人工决定
    public const string ModelFrozen = "model.frozen";
    public const string FreezeRejected = "freeze.rejected";
    public const string TrialApplied = "trial.applied";
    public const string TrialAccepted = "trial.accepted";
    public const string TrialRejected = "trial.rejected";
    public const string ProjectExported = "project.exported";
    public const string ProjectImported = "project.imported";
    public const string StagingWrite = "staging.write";          // 半写恢复证据
}

/// <summary>Append-only, machine-readable event. Sequence is the total order; hash chains events.</summary>
public sealed class ChronoEvent
{
    public long Seq { get; set; }
    public string Type { get; set; } = "";
    public string At { get; set; } = "";                        // ISO 8601
    public string Actor { get; set; } = "system";
    public string RuleVersion { get; set; } = RuleSet.Version;
    public string InputSummary { get; set; } = "";
    public string ParentHashes { get; set; } = "";              // dependency identities
    public JsonElement Payload { get; set; }
    public string Hash { get; set; } = "";
}

public static class JudgmentCode
{
    public const string DepthDuplicate = "DEPTH_DUPLICATE";
    public const string UnconformityBoundary = "UNCONFORMITY_BOUNDARY";
    public const string InvertedWithinSection = "INVERTED_WITHIN_SECTION";
    public const string TiedAnchor = "TIED_ANCHOR";
    public const string DistributionLongTail = "DISTRIBUTION_LONG_TAIL";
    public const string LocalRecompute = "LOCAL_RECOMPUTE";
    public const string ConcurrentFreeze = "CONCURRENT_FREEZE";
    public const string MonotonicSection = "MONOTONIC_SECTION";
    public const string UnconvertibleValue = "UNCONVERTIBLE_VALUE";
    public const string ExclusionRecorded = "EXCLUSION_RECORDED";
    public const string BudgetExhausted = "BUDGET_EXHAUSTED";
    public const string FormatCorrupt = "FORMAT_CORRUPT";
    public const string SemanticUnresolvable = "SEMANTIC_UNRESOLVABLE";
}

/// <summary>Structured, machine-readable judgment (also serialized into events).</summary>
public sealed class Judgment
{
    public string Code { get; set; } = "";
    public string Status { get; set; } = "ok";                  // ok | warning | error | rejected | tied
    public List<string> Subjects { get; set; } = new();
    public string SegmentId { get; set; } = "";
    public string Detail { get; set; } = "";
    public Dictionary<string, object> Evidence { get; set; } = new();
}
