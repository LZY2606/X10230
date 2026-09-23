using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratigraphicAge.Core;

public enum Severity { Info, Warning, Error }
public enum OutcomeKind { Success, IncompleteInput, FormatCorrupt, ResourceBudgetExhausted, SemanticNoSolution, SemanticConflict }
public enum SegmentKind { Normal, Gap, Unconformity, Inverted }
public enum DecisionAction { Accept, Reject, Freeze, ParameterAdjustment }

public sealed record Diagnostic(
    string Code,
    string Message,
    string Location,
    Severity Severity,
    string? ObjectId = null,
    string? RetainedValue = null);

public sealed record OperationOutcome(
    OutcomeKind Kind,
    IReadOnlyList<RecordedEvent> Events,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Kind == OutcomeKind.Success;
}

public sealed record MeasuredValue(
    string Raw,
    double? Value,
    string? Unit,
    string? Datum,
    bool Convertible,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class MeasuredValueFactory
{
    public static MeasuredValue Unconverted(string raw, string? expected, string location, string? objectId = null) =>
        new(raw, null, null, null, false, new[]
        {
            new Diagnostic("VALUE_NOT_CONVERTED",
                $"Value '{raw}' could not be converted to {expected ?? "the target unit"} and was retained in place.",
                location, Severity.Warning, objectId, raw)
        });
}

public sealed record AgeSample(string Label, string Raw, double Value, string Unit, string Datum);

public sealed record DepthDatumPayload(
    string Datum,
    string DepthUnit,
    string Reference,
    MeasuredValue DepthZero);

public sealed record SegmentPayload(
    string SegmentId,
    SegmentKind Kind,
    MeasuredValue TopDepth,
    MeasuredValue BottomDepth,
    string Rationale,
    IReadOnlyList<SourceRef> Sources);

public sealed record SourceRef(string StableId, string Version);

public sealed record AgeDistributionPayload(
    string AnchorId,
    string Label,
    MeasuredValue Depth,
    string Method,
    IReadOnlyList<AgeSample> RawDistribution,
    string AgeUnit,
    string TimeDatum,
    string? ReservoirOffsetId,
    string? EventLayerId,
    bool Included,
    string? DuplicateOf,
    string? TargetAnchorId,
    int Revision,
    string SourceLocation);

public sealed record ReservoirOffsetPayload(
    string OffsetId,
    string Label,
    MeasuredValue Offset,
    IReadOnlyList<string> AppliesToAnchorIds,
    string Basis);

public sealed record EventCorrelationPayload(
    string EventLayerId,
    string Name,
    MeasuredValue Depth,
    string? AnchorId,
    string Correlation,
    IReadOnlyList<string> CandidateAnchorIds,
    bool Included,
    string Basis);

public sealed record ExclusionPayload(
    string DecisionId,
    string TargetId,
    string TargetKind,
    bool Excluded,
    string Reason,
    string Basis);

public sealed record SourceMaterial(
    string StableId,
    string Kind,
    string Label,
    string SourceLocation,
    JsonElement Payload,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record MaterialsImportedPayload(
    string Stage,
    string? ChangedDirectSourceId,
    IReadOnlyList<SourceMaterial> Materials);

public sealed record RuleParameters(
    string Version,
    string PublishedBy,
    double ReservoirCorrectionYears,
    double LowerQuantile,
    double MedianQuantile,
    double UpperQuantile,
    int MaxCandidates,
    int MaxSamplesPerAnchor,
    int MaxMaterials,
    string Interpolation,
    string MonotonicScope);

public sealed record RulesPublishedPayload(RuleParameters Parameters);

public sealed record RateInterval(
    string StableId,
    string FromAnchorId,
    string ToAnchorId,
    double MedianCmPerKyr,
    double? MinimumCmPerKyr,
    double? MaximumCmPerKyr,
    string Unit,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record BandPoint(
    string StableId,
    string AnchorId,
    double DepthMeters,
    double LowerAgeYears,
    double MedianAgeYears,
    double UpperAgeYears,
    string DepthUnit,
    string AgeUnit,
    string DepthDatum,
    string TimeDatum);

public sealed record CandidateSegment(
    SegmentKind Kind,
    string SegmentId,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<BandPoint> Band,
    IReadOnlyList<RateInterval> Rates,
    bool IsBoundary,
    bool Legal,
    string InputDigest,
    IReadOnlyList<string> DependencyMaterialIds,
    string? ReusedFromComputationId,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CandidateModel(
    string ModelId,
    string ComputationId,
    string Label,
    bool Legal,
    int Rank,
    bool EqualRank,
    double Score,
    string SelectionKey,
    IReadOnlyList<CandidateSegment> Segments,
    IReadOnlyList<string> SourceMaterialIds,
    string RuleVersion,
    string InputDigest,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CandidatesComputedPayload(
    string ComputationId,
    string Reason,
    IReadOnlyList<string> ParentEventIds,
    IReadOnlyList<string> ChangedDirectSourceIds,
    IReadOnlyList<string> AffectedSegmentIds,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    string Status);

public sealed record ModelFrozenPayload(string ModelId, string ComputationId, string Scope);

public sealed record DecisionPayload(
    string DecisionId,
    DecisionAction Action,
    string TargetId,
    string Rationale,
    bool Adopted,
    string? ConflictingModelId,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ProjectCreatedPayload(string ProjectId, string Name, string CaseVersion);

public sealed record RecordedEvent(
    string EventId,
    int Sequence,
    string EventType,
    string ProjectId,
    string OccurredAt,
    string Actor,
    string Status,
    string? RuleVersion,
    string? InputDigest,
    IReadOnlyList<string> ParentEventIds,
    JsonElement Payload,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ProjectPackage(
    string PackageVersion,
    string ProjectId,
    string Name,
    string CaseVersion,
    IReadOnlyList<RecordedEvent> Events,
    IReadOnlyList<string> Notes);

public static class ValueParser
{
    public static MeasuredValue Depth(string raw, string location, string? objectId = null)
    {
        var text = raw.Trim();
        var datum = text.Contains("CCSF-A", StringComparison.OrdinalIgnoreCase) ? "CCSF-A"
            : text.Contains("CSF-A", StringComparison.OrdinalIgnoreCase) ? "CSF-A"
            : null;
        if (datum is null)
            return MeasuredValueFactory.Unconverted(raw, "metres below a named depth datum", location, objectId);
        var unit = text.Contains("cm", StringComparison.OrdinalIgnoreCase) ? "cm"
            : text.Contains('m') || text.Contains('M') ? "m" : null;
        var number = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch is '.' or '-' or '+').ToArray());
        if (unit is null || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return MeasuredValueFactory.Unconverted(raw, "metres below " + datum, location, objectId);
        var meters = unit == "cm" ? parsed / 100.0 : parsed;
        var diagnostics = meters < 0
            ? new[] { new Diagnostic("NEGATIVE_DEPTH", "Depth is above the declared zero datum.", location, Severity.Error, objectId, raw) }
            : Array.Empty<Diagnostic>();
        return new MeasuredValue(raw, meters, "m", datum, diagnostics.Length == 0, diagnostics);
    }

    public static MeasuredValue Years(string raw, string location, string? objectId = null)
    {
        var text = raw.Trim();
        if (!text.Contains("BP", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("1950", StringComparison.OrdinalIgnoreCase))
            return MeasuredValueFactory.Unconverted(raw, "years BP1950", location, objectId);
        var number = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch is '.' or '-' or '+').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var years))
            return MeasuredValueFactory.Unconverted(raw, "years BP1950", location, objectId);
        if (text.Contains("ka", StringComparison.OrdinalIgnoreCase)) years *= 1000.0;
        return new MeasuredValue(raw, years, "yr", "BP1950", true, Array.Empty<Diagnostic>());
    }

    public static MeasuredValue OffsetYears(string raw, string location, string? objectId = null)
    {
        var text = raw.Trim();
        var number = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch is '.' or '-' or '+').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var years) ||
            (!text.Contains("yr", StringComparison.OrdinalIgnoreCase) &&
             !text.Contains("year", StringComparison.OrdinalIgnoreCase)))
            return MeasuredValueFactory.Unconverted(raw, "years as a scalar reservoir offset", location, objectId);
        if (text.Contains("ka", StringComparison.OrdinalIgnoreCase)) years *= 1000.0;
        return new MeasuredValue(raw, years, "yr", "reservoir offset", years >= 0,
            years < 0
                ? new[] { new Diagnostic("NEGATIVE_OFFSET", "Reservoir offset must not be negative.", location, Severity.Error, objectId, raw) }
                : Array.Empty<Diagnostic>());
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
