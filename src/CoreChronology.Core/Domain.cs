using System.Text.Json.Serialization;

namespace CoreChronology.Core;

public enum ImportStatus { Imported, ImportedWithDiagnostics, Malformed, SemanticallyUnsolvable, BudgetExhausted }
public enum BoundaryKind { Conformable, CoreBreak, Gap, Unconformity }
public enum DiagnosticSeverity { Information, Warning, Error }

public sealed record QualifiedValue(
    string Raw,
    double? Value,
    string Unit,
    string? NormalizedUnit,
    bool IsConvertible,
    string? Diagnostic)
{
    public static QualifiedValue Parsed(double value, string unit, string? normalizedUnit = null, string? raw = null) =>
        new(raw ?? value.ToString("G17"), value, unit, normalizedUnit ?? unit, true, null);

    public static QualifiedValue Unconvertible(string raw, string unit, string diagnostic) =>
        new(raw, null, unit, null, false, diagnostic);
}

public sealed record DiagnosticEntry(
    string Id,
    string Path,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? MaterialId,
    [property: JsonPropertyName("startLine")] int? StartLine = null,
    [property: JsonPropertyName("startColumn")] int? StartColumn = null);

public sealed record DepthDatum(string Id, string Name, string VerticalDirection, string Unit, string CoordinateReference);

public sealed record CoreSection(
    string Id,
    string Label,
    QualifiedValue TopDepth,
    QualifiedValue BottomDepth,
    string Lithology,
    IReadOnlyList<string> Notes);

public sealed record SegmentBoundary(
    string Id,
    BoundaryKind Kind,
    QualifiedValue UpperDepth,
    QualifiedValue LowerDepth,
    string Name,
    string Basis,
    bool IsExplicitAgeBarrier);

public sealed record AgeSample(double AgeBp, double Weight);

public sealed record AgeDistribution(
    string Id,
    string Method,
    string Material,
    string TimeDatum,
    string AgeUnit,
    IReadOnlyList<AgeSample> Samples,
    string RawRepresentation)
{
    public QuantileSet Quantiles => QuantileSet.FromWeights(Samples.Select(s => s.AgeBp), Samples.Select(s => s.Weight));
    public bool HasLongTail => Quantiles.IsLongTail();
}

public sealed record ReservoirOffset(
    string Id,
    string ReservoirName,
    QualifiedValue Offset,
    AgeDistribution? SupportingDistribution,
    string RawRepresentation,
    string Basis);

public sealed record AgeAnchor(
    string Id,
    string Label,
    string SectionId,
    QualifiedValue Depth,
    AgeDistribution? RawDistribution,
    string? ReservoirOffsetId,
    AgeDistribution? CorrectedDistribution,
    string? EventLayerId,
    bool IncludedInBaseline,
    string? ExclusionReason,
    IReadOnlyList<string> Provenance);

public sealed record EventLayer(
    string Id,
    string Label,
    QualifiedValue Depth,
    string CorrelationId,
    string CorrelationName,
    AgeDistribution? CorrelatedAge,
    string Confidence,
    string RawRepresentation);

public sealed record ExclusionRecord(
    string Id,
    string MaterialId,
    string ObjectKind,
    string Reason,
    string DecisionEventId,
    bool RetainedInSource,
    string RawRepresentation);

public sealed record SourceMaterial(
    DepthDatum DepthDatum,
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<SegmentBoundary> Boundaries,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionRecord> Exclusions,
    string OriginalEncoding,
    string SourceDigest);

public sealed record RuleParameters(
    string Version,
    double MinimumMonotonicProbability,
    int MaximumCandidates,
    string RateUnit,
    string DepthUnit,
    string AgeUnit,
    IReadOnlyDictionary<string, string> RawSettings);

public sealed record ProjectDecision(
    string Id,
    string EventId,
    string Kind,
    string Action,
    bool Accepted,
    string? ConflictWith,
    IReadOnlyList<string> AffectedObjectIds,
    string Rationale,
    string CreatedAt,
    JsonElement Payload);

public sealed record ProjectEvent(
    string Id,
    long Sequence,
    string BranchId,
    IReadOnlyList<string> ParentEventIds,
    string Type,
    string Code,
    string Status,
    string Title,
    string InputSummary,
    string RuleVersion,
    string DependencyDigest,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<string> MachineJudgments,
    string CreatedAt,
    JsonElement Payload);

public sealed record QuantileSet(double Q025, double Q05, double Q50, double Q95, double Q975, double Mean)
{
    public static QuantileSet FromWeights(IEnumerable<double> values, IEnumerable<double> weights)
    {
        var ordered = values.Zip(weights, (v, w) => (v, w)).OrderBy(x => x.v).ToList();
        var total = ordered.Sum(x => x.w);
        if (total <= 0 || ordered.Count == 0)
            return new QuantileSet(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double At(double p)
        {
            var target = total * p;
            var cumulative = 0.0;
            foreach (var (value, weight) in ordered)
            {
                cumulative += weight;
                if (cumulative >= target) return value;
            }
            return ordered[^1].v;
        }
        return new QuantileSet(At(.025), At(.05), At(.5), At(.95), At(.975), ordered.Sum(x => x.v * x.w) / total);
    }

    public bool IsLongTail() => Q95 - Q50 > 3 * Math.Max(0.0001, Q50 - Q05);
}

public sealed record ContinuousBlock(string Id, string Label, double TopDepth, double BottomDepth, IReadOnlyList<string> SectionIds, IReadOnlyList<string> BoundaryIds);

public sealed record RateInterval(
    string Id,
    string BlockId,
    string UpperAnchorId,
    string LowerAnchorId,
    double DepthDistance,
    QuantileSet Rate,
    string Unit,
    string DependencyDigest,
    bool Valid,
    string? InvalidReason);

public sealed record AgeBandPoint(string AnchorId, double Depth, double Q05, double Q50, double Q95);

public sealed record BlockModel(
    string BlockId,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<AgeBandPoint> Band,
    IReadOnlyList<string> Diagnostics);

public sealed record ModelCandidate(
    string Id,
    string Method,
    string RuleVersion,
    bool IsLegal,
    bool Adopted,
    int Rank,
    string Score,
    string? RejectionReason,
    IReadOnlyList<BlockModel> Blocks,
    IReadOnlyList<string> SelectedAnchorIds,
    IReadOnlyList<string> DependencyDigests,
    string InputDigest,
    string DeterministicKey);

public sealed record DisplayValue(
    string Id,
    string Label,
    string? Value,
    string Unit,
    string? Datum,
    string? ValidRange,
    string? InvalidReason,
    string SourceMaterialId,
    string SourceEventId,
    string Kind,
    int SortOrder);

public sealed record ChronologyResult(
    ImportStatus Status,
    IReadOnlyList<DiagnosticEntry> Diagnostics,
    IReadOnlyList<ContinuousBlock> Blocks,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<ProjectEvent> Events,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<string> UnchangedObjectIds,
    string InputDigest,
    string? FrozenRevisionId,
    string? BranchId);

public sealed record ProjectState(
    string Id,
    string Name,
    string CurrentBranchId,
    string? FrozenRevisionId,
    long Sequence,
    SourceMaterial Material,
    RuleParameters Parameters,
    IReadOnlyList<ProjectEvent> Events,
    IReadOnlyList<ProjectDecision> Decisions,
    ChronologyResult? Result,
    string CreatedAt,
    string UpdatedAt);

public sealed record ProjectBranch(
    string Id,
    string Name,
    string ParentBranchId,
    string? FrozenRevisionId,
    long CreatedAtSequence,
    IReadOnlyList<string> EventIds);
