using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>All event kinds. Events are the only source of truth; derived states are folds.</summary>
public static class EventKinds
{
    public const string ImportBundle = "import_bundle";
    public const string ReviseParameter = "revise_parameter";
    public const string AmendSource = "amend_source";
    public const string Decide = "decide";
    public const string ProposeAnchor = "propose_anchor";
    public const string FreezeModel = "freeze_model";
    public const string RejectedOperation = "rejected_operation";
    public const string Judgment = "judgment";
    public const string ArchiveBaseline = "archive_baseline";
    public const string SingleSourceMutation = "single_source_mutation";
}

/// <summary>Append-only immutable event. Identity, branch, ordering and provenance all live here.</summary>
public sealed record ChronoEvent(
    string EventId,
    string Kind,
    string Branch,
    long Seq,
    string? ParentEventId,
    string? ForkPointEventId,
    string Author,
    string OccurredAt,
    JsonObject Payload,
    string InputDigest,
    string RuleVersion,
    string ParentDependencyHash)
{
    public JsonObject ToNode() => new()
    {
        ["eventId"] = EventId,
        ["kind"] = Kind,
        ["branch"] = Branch,
        ["seq"] = Seq,
        ["parentEventId"] = ParentEventId,
        ["forkPointEventId"] = ForkPointEventId,
        ["author"] = Author,
        ["occurredAt"] = OccurredAt,
        ["payload"] = Payload?.DeepClone(),
        ["inputDigest"] = InputDigest,
        ["ruleVersion"] = RuleVersion,
        ["parentDependencyHash"] = ParentDependencyHash
    };
}
