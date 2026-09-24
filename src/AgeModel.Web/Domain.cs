using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel.Web;

public enum Severity { Info, Warning, Error }
public enum JudgmentStatus { Active, Resolved, Conflict, Excluded, Adopted, Rejected }
public enum DerivationStatus { Ok, SemanticNoSolution, ResourceBudgetExhausted }
public enum CandidateStatus { Valid, Invalid, Excluded, Frozen, Conflicting }
public enum SegmentKind { Continuous, Gap, Unconformity, Inverted }

public sealed record Quantity(
    string? RawText,
    double? Value,
    string Unit,
    string? Datum,
    string? Basis,
    double? NormalizedValue,
    string? NormalizedUnit,
    bool ConversionOk,
    string? ConversionDiagnostic)
{
    public static Quantity Parse(JsonElement? element, string expectedKind)
    {
        if (element is not JsonElement json || json.ValueKind == JsonValueKind.Null)
            return new Quantity(null, null, "", null, null, null, null, false, "Missing quantity");
        if (json.ValueKind == JsonValueKind.Number)
        {
            var number = json.GetDouble();
            return new Quantity(number.ToString("G17"), number, "", null, null, number, expectedKind == "depth" ? "m" : expectedKind, false, "Unit is required");
        }
        if (json.ValueKind != JsonValueKind.Object)
            return new Quantity(json.GetRawText(), null, "", null, null, null, null, false, "Quantity must be an object");

        var rawText = json.TryGetProperty("rawText", out var rt) ? rt.GetString() : json.GetRawText();
        double? value = json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var unit = json.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
        var datum = json.TryGetProperty("datum", out var d) ? d.GetString() : null;
        var basis = json.TryGetProperty("basis", out var b) ? b.GetString() : null;
        if (value is null || string.IsNullOrWhiteSpace(unit))
            return new Quantity(rawText, value, unit, datum, basis, null, null, false, "Value and unit are required");

        var (normalized, normalizedUnit, ok, diagnostic) = expectedKind switch
        {
            "depth" => NormalizeDepth(value.Value, unit),
            "age" => NormalizeAge(value.Value, unit),
            "rate" => NormalizeRate(value.Value, unit),
            _ => (value, unit, true, (string?)null)
        };
        return new Quantity(rawText, value, unit, datum, basis, normalized, normalizedUnit, ok, diagnostic);
    }

    private static (double?, string?, bool, string?) NormalizeDepth(double value, string unit) => unit.Trim() switch
    {
        "m" or "m bsf" or "m below sea floor" => (value, "m", true, null),
        "cm" => (value / 100d, "m", true, null),
        var u => (null, null, false, $"Depth unit '{u}' cannot be normalized; raw value retained at its source position")
    };

    private static (double?, string?, bool, string?) NormalizeAge(double value, string unit) => unit.Trim() switch
    {
        "a BP" or "yr BP" or "years BP" or "a" => (value, "a BP", true, null),
        "ka BP" => (value * 1000d, "a BP", true, null),
        "14C a BP" => (value, "14C a BP", true, null),
        "reservoir-corrected 14C a BP" => (value, "reservoir-corrected 14C a BP", true, null),
        var u => (null, null, false, $"Age unit '{u}' cannot be normalized; raw value retained at its source position")
    };

    private static (double?, string?, bool, string?) NormalizeRate(double value, string unit) => unit.Trim() switch
    {
        "a/m" or "yr/m" or "years/m" => (value, "a/m", true, null),
        "ka/m" => (value * 1000d, "a/m", true, null),
        var u => (null, null, false, $"Rate unit '{u}' cannot be normalized; raw value retained")
    };
}

public sealed record AgeDistribution(
    string Id,
    IReadOnlyList<double> Samples,
    string Unit,
    string Basis,
    string? RawText,
    string? ReservoirOffsetId)
{
    public Quantiles Quantiles() => Statistics.Quantiles(Samples);
}

public sealed record ReservoirOffset(
    string Id,
    Quantity Offset,
    Quantity Uncertainty,
    string Material,
    string Source,
    string TimeBasis)
{ }

public sealed record CoreSegment(
    string Id,
    string Label,
    SegmentKind Kind,
    Quantity Top,
    Quantity Bottom,
    string Source,
    string? Notes)
{ }

public sealed record AgeAnchor(
    string Id,
    string Label,
    Quantity Depth,
    AgeDistribution RawAge,
    string Material,
    string Source,
    string? EventLayerId,
    string? ReservoirOffsetId,
    IReadOnlyList<string> Notes,
    string? AddedByEventId)
{ }

public sealed record EventLayer(
    string Id,
    string Label,
    Quantity TopDepth,
    Quantity Thickness,
    string CorrelationKey,
    string Confidence,
    string Source)
{ }

public sealed record ExclusionDecision(
    string Id,
    string SubjectType,
    string SubjectId,
    bool Excluded,
    string Reason,
    string DecidedBy,
    string DecidedAt,
    string SourcePointer,
    string Basis)
{ }

public sealed class RuleSet
{
    public string RuleVersion { get; init; } = "rules-2026.1";
    public double LowerTailProbability { get; init; } = 0.05;
    public double UpperTailProbability { get; init; } = 0.95;
    public int MaxCandidates { get; init; } = 12;
    public string MonotonicBasis { get; init; } = "Age is non-decreasing downward only between controls joined by a Continuous segment.";
    public string DistributionBasis { get; init; } = "Full empirical radiometric distributions are retained; quantiles are derived, never replaced by a mean.";
}

public sealed class CasePackage
{
    public string SchemaVersion { get; init; } = "core-age-model/v1";
    public string CaseId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string DepthDatum { get; init; } = "";
    public string TimeDatum { get; init; } = "";
    public RuleSet InitialRuleSet { get; init; } = new();
    public List<CoreSegment> Segments { get; init; } = new();
    public List<AgeAnchor> Anchors { get; init; } = new();
    public List<ReservoirOffset> ReservoirOffsets { get; init; } = new();
    public List<EventLayer> EventLayers { get; init; } = new();
    public List<ExclusionDecision> ExclusionDecisions { get; init; } = new();
    public List<ReplayOperation> Replay { get; init; } = new();
    public List<CounterexampleTemplate> CounterexampleTemplates { get; init; } = new();
}

public sealed class ReplayOperation
{
    public string EventId { get; init; } = "";
    public string Type { get; init; } = "";
    public string? RuleVersion { get; init; }
    public double? LowerTailProbability { get; init; }
    public double? UpperTailProbability { get; init; }
    public int? MaxCandidates { get; init; }
    public string? DirectSourceId { get; init; }
    public string? SourceKind { get; init; }
    public string? Field { get; init; }
    public JsonElement? NewValue { get; init; }
    public string? DecisionId { get; init; }
    public string? DecisionAction { get; init; }
    public string? CandidateId { get; init; }
    public string? Rationale { get; init; }
    public AgeAnchor? Anchor { get; init; }
    public string? ExpectedLastEventId { get; init; }
}

public sealed class CounterexampleTemplate
{
    public string BranchId { get; init; } = "";
    public string Label { get; init; } = "";
    public string ScenarioCode { get; init; } = "";
    public string ChangeKind { get; init; } = "";
    public string DirectSourceId { get; init; } = "";
    public string? SourceKind { get; init; }
    public string? Field { get; init; }
    public JsonElement? NewValue { get; init; }
    public AgeAnchor? Anchor { get; init; }
    public string ExpectedJudgment { get; init; } = "";
}

public sealed record Diagnostic(string Location, string Code, string Message, Severity Severity);

public sealed record Judgment(
    string Id,
    string Code,
    Severity Severity,
    JudgmentStatus Status,
    string Message,
    string Basis,
    string EventId,
    IReadOnlyList<string> AffectedIds,
    string? SourcePointer,
    string? ScenarioCode);

public sealed record Quantiles(double Q05, double Q50, double Q95, double Minimum, double Maximum, int SampleCount);

public sealed record DepthInterval(double? Top, double? Bottom, string Unit, bool Valid, string? FailureReason);
public sealed record RateInterval(string Id, string SegmentId, string FromAnchorId, string ToAnchorId,
    DepthInterval DepthInterval, double? Minimum, double? Median, double? Maximum, string Unit,
    bool Valid, string? FailureReason, string LocalDependencyKey, string ValueHash);

public sealed record Candidate(
    string Id,
    string LineageId,
    string Label,
    CandidateStatus Status,
    int Score,
    string TieGroup,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<string> ControlIds,
    IReadOnlyList<Point> MedianCurve,
    IReadOnlyList<BandPoint> UncertaintyBand,
    string? FailureReason,
    bool Frozen,
    string? FrozenAtEventId);

public sealed record Point(double Depth, double? Age, string Unit);
public sealed record BandPoint(double Depth, double? Young, double? Median, double? Old, string Unit);

public sealed record DisplayValue(
    string Id,
    string Label,
    string ObjectId,
    string Kind,
    string? Value,
    string? NumericValue,
    string Unit,
    string? Datum,
    string? Basis,
    string? ValidInterval,
    string? FailureReason,
    string SourceEventId,
    string ValueHash);

public sealed record AnchorState(
    string Id,
    string Label,
    double? DepthM,
    string RawDepth,
    Quantiles? RawQuantiles,
    Quantiles? CorrectedQuantiles,
    string AgeUnit,
    string TimeBasis,
    bool Excluded,
    string? ExclusionReason,
    string? ReservoirOffsetId,
    string? EventLayerId,
    IReadOnlyList<Judgment> Judgments);

public sealed record SegmentState(
    string Id,
    string Label,
    SegmentKind Kind,
    double? TopM,
    double? BottomM,
    string Unit,
    bool Valid,
    string? FailureReason);

public sealed record FrozenSnapshot(
    string FreezeEventId,
    string CandidateId,
    string CandidateLabel,
    string FrozenAtEventId,
    Candidate Candidate,
    IReadOnlyList<string> ParentDependencies,
    string RuleVersion);

public sealed record FrozenImpact(
    string FrozenCandidateId,
    string NewAnchorId,
    IReadOnlyList<string> ChangedLocalDependencyKeys,
    IReadOnlyList<string> UnchangedDependencyKeys,
    string? NewConflictCode);

public sealed record DerivationResult(
    DerivationStatus Status,
    string CaseId,
    string BranchId,
    string EventId,
    long Sequence,
    string RuleVersion,
    string InputSummary,
    IReadOnlyList<string> ParentDependencies,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments,
    IReadOnlyList<SegmentState> Segments,
    IReadOnlyList<AnchorState> Anchors,
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<DisplayValue> DisplayValues,
    IReadOnlyList<FrozenSnapshot> FrozenSnapshots,
    IReadOnlyList<FrozenImpact> FrozenImpacts,
    IReadOnlyList<string> AdoptedCandidateIds,
    IReadOnlyList<string> RejectedCandidateIds,
    bool HasConflictingDecisions);

public sealed record CaseEvent(
    string EventId,
    string BranchId,
    long Sequence,
    string Type,
    JsonElement Payload,
    string InputSummary,
    IReadOnlyList<string> ParentDependencies,
    string RuleVersion,
    string CreatedAt,
    string ContentHash)
{
    public T? PayloadAs<T>() => JsonSerializer.Deserialize<T>(Payload.GetRawText(), AgeJson.Options);
}

public sealed record BranchState(string BranchId, string Label, string BaseEventId, string? ScenarioCode, long Sequence, string LastEventId);

public sealed record CaseSnapshot(
    CasePackage Package,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<BranchState> Branches,
    DerivationResult Derivation,
    DerivationResult? BaselineDerivation);

public sealed class ProjectExport
{
    public string SchemaVersion { get; set; } = "core-age-model-export/v1";
    public string ExportedAt { get; set; } = "";
    public List<CaseEvent> Events { get; set; } = new();
    public List<BranchRecord> Branches { get; set; } = new();
    public List<BranchEventRecord> BranchEvents { get; set; } = new();
}

public sealed record BranchRecord(string BranchId, string CaseId, string Label, string BaseEventId, string? ScenarioCode, long Sequence, string LastEventId);
public sealed record BranchEventRecord(string BranchId, string EventId, long Sequence);

public static class Statistics
{
    public static Quantiles Quantiles(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new Quantiles(0, 0, 0, 0, 0, 0);
        var sorted = values.OrderBy(v => v).ToArray();
        return new Quantiles(Quantile(sorted, 0.05), Quantile(sorted, 0.50), Quantile(sorted, 0.95), sorted[0], sorted[^1], sorted.Length);
    }

    private static double Quantile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CasePackage))]
[JsonSerializable(typeof(ProjectExport))]
internal partial class AgeJsonContext : JsonSerializerContext { }

public static class AgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
