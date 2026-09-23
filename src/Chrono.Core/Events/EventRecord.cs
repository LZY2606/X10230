using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Chrono.Core.Model;

namespace Chrono.Core.Events;

/// <summary>
/// Append-only event-sourcing record. Everything that changes project state is one of these.
/// Ids are stable across export/import; sequence is per-branch and dense.
/// </summary>
public sealed record EventRecord(
    string Id,
    string Branch,
    long Seq,
    string Type,
    string? ParentEventId,
    string Actor,
    DateTimeOffset CreatedAt,
    string PayloadJson,
    string InputSummary,
    string RuleVersion)
{
    /// <summary>Checksum over canonical payload + parent id; verified on import/replay.</summary>
    public string Hash { get; init; } = "";
}

public static class EventTypes
{
    public const string ProjectCreated = "project_created";
    public const string MaterialImported = "material_imported";
    public const string RuleVersionSet = "rule_version_set";
    public const string ParameterRevised = "parameter_revised";
    public const string SourceAmended = "source_amended";
    public const string ExclusionDecided = "exclusion_decided";
    public const string CorrelationChosen = "correlation_chosen";
    public const string ModelDerived = "model_derived";
    public const string CandidateAdopted = "candidate_adopted";
    public const string CandidateRejected = "candidate_rejected";
    public const string ModelFrozen = "model_frozen";
    public const string BranchForked = "branch_forked";
    public const string ImportBundle = "import_bundle";
}

/// <summary>Machine-readable judgment produced by the rules engine for a specific situation.</summary>
public sealed record RuleJudgment(
    string RuleCode,
    string Situation,
    string Status,
    string Reason,
    IReadOnlyList<string> AffectedObjectIds,
    string? BoundaryId = null)
{
    public const string MonotoneOnlyWithinContinuous = "MONOTONE_ONLY_WITHIN_CONTINUOUS";
    public const string GapIsExplicitBoundary = "GAP_IS_EXPLICIT_BOUNDARY";
    public const string DistributionNotMean = "DISTRIBUTION_NOT_MEAN";
    public const string KeepAllEquallyGood = "KEEP_ALL_EQUALLY_GOOD";
}

/// <summary>Structured outcome of running the rules over one project state.</summary>
public sealed record DerivationResult(
    string DerivationId,
    string Branch,
    long TriggerSeq,
    string RuleVersion,
    string InputSummary,
    IReadOnlyList<string> ParentEventIds,
    IReadOnlyList<TrackResult> Tracks,
    IReadOnlyList<RuleJudgment> Judgments,
    IReadOnlyList<string> Diagnostics,
    string Status);
