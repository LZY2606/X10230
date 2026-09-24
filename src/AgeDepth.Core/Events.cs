namespace AgeDepth.Core;

/// <summary>Machine-readable judgment emitted by the rules engine.</summary>
public record JudgmentEvent(string Type, string Rule, string Outcome, IReadOnlyDictionary<string, string> Facts)
{
    public static JudgmentEvent Of(string type, string rule, string outcome, params (string k, string v)[] facts) =>
        new(type, rule, outcome, facts.ToDictionary(f => f.k, f => f.v));
}

/// <summary>Append-only event log entry. Every state change is one of these.</summary>
public record EventRecord(
    long Seq,
    string Id,
    string Type,
    string RuleVersion,
    string InputDigest,
    IReadOnlyList<string> Parents,
    string PayloadJson,
    DateTimeOffset Timestamp);

public static class EventTypes
{
    public const string CaseImported = "case/imported";
    public const string MaterialAdded = "material/added";
    public const string DecisionRecorded = "decision/recorded";
    public const string DecisionConflict = "decision/conflict";
    public const string ModelBuilt = "model/built";
    public const string ModelBuildFailed = "model/build-failed";
    public const string ModelFrozen = "model/frozen";
    public const string ModelFreezeConflict = "model/freeze-conflict";
    public const string AnchorCompared = "model/anchor-compared";
    public const string Judgment = "judgment";
    public const string ArchiveExported = "archive/exported";
    public const string ArchiveImported = "archive/imported";
    public const string RecoveryPurge = "recovery/purged-half-written-batch";
}
