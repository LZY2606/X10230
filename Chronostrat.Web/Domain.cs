using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chronostrat;

[JsonDerivedType(typeof(AnchorMaterial), "age-anchor")]
[JsonDerivedType(typeof(EventLayerMaterial), "event-layer")]
[JsonDerivedType(typeof(DepthDatumMaterial), "depth-datum")]
[JsonDerivedType(typeof(CoreSectionMaterial), "core-section")]
[JsonDerivedType(typeof(CoreSegmentMaterial), "core-segment")]
[JsonDerivedType(typeof(SegmentBoundaryMaterial), "segment-boundary")]
[JsonDerivedType(typeof(ReservoirOffsetMaterial), "reservoir-offset")]
public abstract record Material
{
    public required string StableId { get; init; }
    public int Version { get; init; } = 1;
    public required string SourceId { get; init; }
    public abstract string Kind { get; }
    public bool Excluded { get; init; }
    public string? ExclusionReason { get; init; }
    public string? ExclusionEventId { get; init; }
    public string Hash { get; init; } = Hashing.Empty;
}

public sealed record Quantity
{
    public double? NormalizedValue { get; init; }
    public required string Unit { get; init; }
    public string? Basis { get; init; }
    public required string OriginalRepresentation { get; init; }
    public bool Convertible { get; init; } = true;
    public string? Diagnostic { get; init; }

    public static Quantity DepthMbsf(double meters, string? original = null, string sourceId = "") =>
        new() { NormalizedValue = meters, Unit = "m", Basis = "mbsf", OriginalRepresentation = original ?? $"{meters.ToString("0.###", CultureInfo.InvariantCulture)} mbsf", Convertible = true };

    public static Quantity AgeBp(double years, string? original = null) =>
        new() { NormalizedValue = years, Unit = "a", Basis = "BP1950", OriginalRepresentation = original ?? $"{years.ToString("0.#", CultureInfo.InvariantCulture)} a BP1950", Convertible = true };

    public static Quantity Unconvertible(string unit, string original, string diagnostic) =>
        new() { NormalizedValue = null, Unit = unit, Basis = null, OriginalRepresentation = original, Convertible = false, Diagnostic = diagnostic };
}

public sealed record AgeDistribution
{
    public required string DistributionId { get; init; }
    public required string Method { get; init; }
    public required string Unit { get; init; } = "a";
    public required string Basis { get; init; } = "BP1950";
    public required IReadOnlyList<double> Samples { get; init; }
    public required string OriginalRepresentation { get; init; }
    public double? ReportedMean { get; init; }
    public bool LongTail { get; init; }

    public double Quantile(double q) => Statistics.Quantile(Samples, q);
}

public sealed record ReservoirOffsetMaterial : Material
{
    public override string Kind => "reservoir-offset";
    public required string AnchorId { get; init; }
    public required Quantity Offset { get; init; }
    public required string CorrectionBasis { get; init; }
}

public sealed record DepthDatumMaterial : Material
{
    public override string Kind => "depth-datum";
    public required string CoreId { get; init; }
    public required Quantity Depth { get; init; }
    public required string DatumName { get; init; }
}

public sealed record CoreSectionMaterial : Material
{
    public override string Kind => "core-section";
    public required string CoreId { get; init; }
    public required Quantity TopDepth { get; init; }
    public required Quantity BottomDepth { get; init; }
    public required string Lithology { get; init; }
}

public sealed record SegmentBoundaryMaterial : Material
{
    public override string Kind => "segment-boundary";
    public required Quantity Depth { get; init; }
    public required string BoundaryKind { get; init; }
    public double? GapThicknessM { get; init; }
    public required string Interpretation { get; init; }
}

public sealed record CoreSegmentMaterial : Material
{
    public override string Kind => "core-segment";
    public required Quantity TopDepth { get; init; }
    public required Quantity BottomDepth { get; init; }
    public required string SegmentKind { get; init; }
    public required string Interpretation { get; init; }
}

public sealed record EventLayerMaterial : Material
{
    public override string Kind => "event-layer";
    public required string EventName { get; init; }
    public required Quantity Depth { get; init; }
    public required string Correlation { get; init; }
    public required string CorrelationBasis { get; init; }
    public AgeDistribution? CorrelatedAge { get; init; }
}

public sealed record AnchorMaterial : Material
{
    public override string Kind => "age-anchor";
    public required Quantity Depth { get; init; }
    public required AgeDistribution Age { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public string? EventLayerId { get; init; }
    public required string AnchorKind { get; init; }
    public required string LaboratoryOrEventCode { get; init; }
}

public sealed record RuleParameterSet
{
    public required string ParameterId { get; init; }
    public int Version { get; init; }
    public required string DepthUnit { get; init; }
    public required string AgeBasis { get; init; }
    public double MaximumSedimentationRateCmPerKyr { get; init; }
    public required double[] UncertaintyProbabilities { get; init; }
    public int MaxAnchorCombinations { get; init; } = 256;
    public required string ChangeSummary { get; init; }
}

public sealed record DiagnosticItem
{
    public required string Code { get; init; }
    public required string Severity { get; init; }
    public required string Location { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> AffectedObjects { get; init; } = Array.Empty<string>();
}

public enum SolveOutcomeKind { Ok, SemanticNoSolution, ResourceBudgetExhausted }

public sealed record RateInterval
{
    public required string StableId { get; init; }
    public required string SegmentId { get; init; }
    public required string UpperAnchorId { get; init; }
    public required string LowerAnchorId { get; init; }
    public double LowCmPerKyr { get; init; }
    public double MedianCmPerKyr { get; init; }
    public double HighCmPerKyr { get; init; }
    public string Unit => "cm/ka";
    public string? Diagnostic { get; init; }
}

public sealed record UncertaintyBandPoint
{
    public required double DepthMbsf { get; init; }
    public required double LowAgeBp { get; init; }
    public required double MedianAgeBp { get; init; }
    public required double HighAgeBp { get; init; }
    public string DepthUnit => "m";
    public string DepthBasis => "mbsf";
    public string AgeUnit => "a";
    public string AgeBasis => "BP1950";
}

public sealed record CandidateModel
{
    public required string CandidateId { get; init; }
    public required string BranchId { get; init; }
    public required string SegmentId { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required IReadOnlyList<UncertaintyBandPoint> Band { get; init; }
    public required double Score { get; init; }
    public int TieRank { get; init; }
    public bool EquallyOptimal { get; init; }
    public string Status { get; init; } = "derived";
    public string? ParentCandidateId { get; init; }
    public required string ParameterId { get; init; }
    public required string InputSummary { get; init; }
    public required IReadOnlyList<string> ParentDependencies { get; init; }
    public required string CreatedByEventId { get; init; }
    public IReadOnlyList<DiagnosticItem> Diagnostics { get; init; } = Array.Empty<DiagnosticItem>();
}

public sealed record SolveResult
{
    public required SolveOutcomeKind Outcome { get; init; }
    public IReadOnlyList<CandidateModel> Candidates { get; init; } = Array.Empty<CandidateModel>();
    public IReadOnlyList<DiagnosticItem> Diagnostics { get; init; } = Array.Empty<DiagnosticItem>();
}

public sealed record WorkspaceState
{
    public required string ProjectId { get; init; }
    public required IReadOnlyList<Material> Materials { get; init; }
    public required IReadOnlyList<RuleParameterSet> Parameters { get; init; }
    public required IReadOnlyList<CandidateModel> Candidates { get; init; }
    public required string ActiveParameterId { get; init; }
}

public sealed record EventRecord
{
    public long Sequence { get; init; }
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string BranchId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? TargetId { get; init; }
    public required string PayloadJson { get; init; }
    public required IReadOnlyList<string> ParentEventIds { get; init; }
    public required string ParameterVersion { get; init; }
    public required string InputSummary { get; init; }
    public required string Status { get; init; }
}

public sealed record DecisionRecord
{
    public required string DecisionId { get; init; }
    public required string EventId { get; init; }
    public required string BranchId { get; init; }
    public required string Action { get; init; }
    public required string TargetId { get; init; }
    public required string Rationale { get; init; }
    public required string ParameterId { get; init; }
    public bool Adopted { get; init; }
    public string? ConflictWith { get; init; }
}

public sealed record JudgmentRecord
{
    public required string JudgmentId { get; init; }
    public required string ProbeId { get; init; }
    public required string Principle { get; init; }
    public required string Result { get; init; }
    public bool Adopted { get; init; }
    public required string Scope { get; init; }
    public required string Basis { get; init; }
    public required IReadOnlyList<string> AffectedObjects { get; init; }
    public required IReadOnlyList<string> EventIds { get; init; }
    public required string MachineEvent { get; init; }
}

public static class Statistics
{
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var position = (sorted.Length - 1) * Math.Clamp(q, 0, 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

public static class Hashing
{
    public const string Empty = "sha256:0";
    public static string Sha256(string value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(WorkspaceState))]
[JsonSerializable(typeof(Material))]
[JsonSerializable(typeof(AnchorMaterial))]
[JsonSerializable(typeof(EventLayerMaterial))]
[JsonSerializable(typeof(DepthDatumMaterial))]
[JsonSerializable(typeof(CoreSectionMaterial))]
[JsonSerializable(typeof(CoreSegmentMaterial))]
[JsonSerializable(typeof(SegmentBoundaryMaterial))]
[JsonSerializable(typeof(ReservoirOffsetMaterial))]
[JsonSerializable(typeof(RuleParameterSet))]
[JsonSerializable(typeof(CandidateModel))]
[JsonSerializable(typeof(EventRecord))]
[JsonSerializable(typeof(DecisionRecord))]
[JsonSerializable(typeof(JudgmentRecord))]
[JsonSerializable(typeof(DiagnosticItem))]
public partial class AppJsonContext : JsonSerializerContext;
