namespace AgeModel.Core;

/// <summary>Machine-readable event. Append-only; the replayable evidence trail.</summary>
public sealed record DomainEvent(
    long Seq,
    string Id,
    string Kind,
    string PayloadJson,
    string At,
    string Actor,
    string BatchId);

/// <summary>Well-known event kinds. Judgment rules emit these as structured state.</summary>
public static class Ev
{
    public const string MaterialImported = "material.imported";
    public const string MaterialRevised = "material.revised";
    public const string NormalizationFailed = "quantity.normalization_failed";
    public const string SegmentationDerived = "segmentation.derived";
    public const string BoundaryDeclared = "boundary.declared";
    public const string DepthDuplicate = "anchor.depth_duplicate";
    public const string TieDetected = "anchor.tie_detected";
    public const string InversionDetected = "segment.inversion_detected";
    public const string LongTailDetected = "distribution.long_tail";
    public const string DistributionPreserved = "distribution.preserved";
    public const string MonotonicityJudged = "monotonicity.judged";
    public const string CandidatesRetained = "candidates.retained";
    public const string ModelDerived = "model.derived";
    public const string ModelFrozen = "model.frozen";
    public const string FreezeConflict = "model.freeze_conflict";
    public const string AnchorExcluded = "anchor.excluded";
    public const string ExclusionConflict = "anchor.exclusion_conflict";
    public const string LocalRecompute = "recompute.local";
    public const string BudgetExhausted = "result.budget_exhausted";
    public const string FormatCorrupt = "result.format_corrupt";
    public const string Unsolvable = "result.unsolvable";
    public const string ExportCreated = "export.created";
    public const string ImportCompleted = "import.completed";
    public const string HistoryCheckedOut = "history.checked_out";
    public const string ParameterRevised = "parameter.revised";
    public const string ImpactCompared = "model.impact_compared";
}

/// <summary>Three distinct failure outcomes, never collapsed into one.</summary>
public enum BuildStatus { Ok, BudgetExhausted, FormatCorrupt, Unsolvable }

public sealed record BuildResult(
    BuildStatus Status,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<DomainEvent> Events)
{
    public static BuildResult Failed(BuildStatus status, IReadOnlyList<Diagnostic> diags, IReadOnlyList<DomainEvent> events) =>
        new(status, Array.Empty<Segment>(), Array.Empty<ModelCandidate>(), diags, events);
}
