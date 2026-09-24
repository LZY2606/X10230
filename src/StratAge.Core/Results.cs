using System.Text.Json.Serialization;

namespace StratAge.Core;

public enum Severity { Error, Warning, Info }

public enum OutcomeClass
{
    Ok,
    BudgetExhausted,   // resource budget ran out before the search finished
    FormatCorrupt,     // input bytes/structure are broken
    SemanticUnresolvable // structurally fine, but no legal model exists
}

public sealed record Diagnostic
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("severity")] public required Severity Severity { get; init; }
    [JsonPropertyName("location")] public required string Location { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("ruleId")] public string? RuleId { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
}

public sealed record ModelingParameters
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("interpolation")] public required string Interpolation { get; init; } // linear | pchip
    [JsonPropertyName("applyReservoir")] public bool ApplyReservoir { get; init; } = true;
    [JsonPropertyName("sampleCount")] public int SampleCount { get; init; } = 2000;
    [JsonPropertyName("hardInversionThreshold")] public double HardInversionThreshold { get; init; } = 0.5;
    [JsonPropertyName("uncertainOverlapThreshold")] public double UncertainOverlapThreshold { get; init; } = 0.02;
    [JsonPropertyName("rateUnit")] public string RateUnit { get; init; } = "cm/yr";
    [JsonPropertyName("maxCandidates")] public int MaxCandidates { get; init; } = 64;
    [JsonPropertyName("budgetOps")] public long BudgetOps { get; init; } = 500_000;
    [JsonPropertyName("note")] public string? Note { get; init; }

    public ModelingParameters With(string? interpolation = null, bool? applyReservoir = null,
        int? sampleCount = null, string? note = null, string? version = null) => this with
    {
        Version = version ?? Version,
        Interpolation = interpolation ?? Interpolation,
        ApplyReservoir = applyReservoir ?? ApplyReservoir,
        SampleCount = sampleCount ?? SampleCount,
        Note = note ?? Note
    };
}

public sealed record IntervalRate
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("segmentId")] public required string SegmentId { get; init; }
    [JsonPropertyName("topDepthM")] public required double TopDepthM { get; init; }
    [JsonPropertyName("baseDepthM")] public required double BaseDepthM { get; init; }
    [JsonPropertyName("medianCmPerYr")] public required double MedianCmPerYr { get; init; }
    [JsonPropertyName("p05CmPerYr")] public required double P05CmPerYr { get; init; }
    [JsonPropertyName("p95CmPerYr")] public required double P95CmPerYr { get; init; }
}

public sealed record UncertaintyBandPoint
{
    [JsonPropertyName("depthM")] public required double DepthM { get; init; }
    [JsonPropertyName("p05Ka")] public required double P05Ka { get; init; }
    [JsonPropertyName("p50Ka")] public required double P50Ka { get; init; }
    [JsonPropertyName("p95Ka")] public required double P95Ka { get; init; }
    [JsonPropertyName("segmentId")] public required string SegmentId { get; init; }
}

public sealed record CandidateModel
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("rank")] public required int Rank { get; init; }
    [JsonPropertyName("score")] public required double Score { get; init; }
    [JsonPropertyName("equalOptimal")] public required bool EqualOptimal { get; init; }
    [JsonPropertyName("interpolation")] public required string Interpolation { get; init; }
    [JsonPropertyName("chosenAnchors")] public required IReadOnlyList<string> ChosenAnchors { get; init; }
    [JsonPropertyName("tieChoices")] public required IReadOnlyDictionary<string, string> TieChoices { get; init; }
    [JsonPropertyName("rates")] public required IReadOnlyList<IntervalRate> Rates { get; init; }
    [JsonPropertyName("band")] public required IReadOnlyList<UncertaintyBandPoint> Band { get; init; }
    [JsonPropertyName("violations")] public required IReadOnlyList<Diagnostic> Violations { get; init; }
}

public sealed record AnchorEvaluation
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("depthM")] public double? DepthM { get; init; }
    [JsonPropertyName("medianKa")] public double? MedianKa { get; init; }
    [JsonPropertyName("p05Ka")] public double? P05Ka { get; init; }
    [JsonPropertyName("p95Ka")] public double? P95Ka { get; init; }
    [JsonPropertyName("longTail")] public required bool LongTail { get; init; }
    [JsonPropertyName("usable")] public required bool Usable { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("unit")] public string? Unit { get; init; }
    [JsonPropertyName("timeBasis")] public string? TimeBasis { get; init; }
}

public sealed record ModelResult
{
    [JsonPropertyName("outcome")] public required OutcomeClass Outcome { get; init; }
    [JsonPropertyName("parameterVersion")] public required string ParameterVersion { get; init; }
    [JsonPropertyName("inputDigest")] public required string InputDigest { get; init; }
    [JsonPropertyName("candidates")] public required IReadOnlyList<CandidateModel> Candidates { get; init; }
    [JsonPropertyName("anchors")] public required IReadOnlyList<AnchorEvaluation> Anchors { get; init; }
    [JsonPropertyName("diagnostics")] public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    [JsonPropertyName("opsUsed")] public required long OpsUsed { get; init; }
    [JsonPropertyName("budgetOps")] public required long BudgetOps { get; init; }
    [JsonPropertyName("effectiveDepthRangeM")] public double[]? EffectiveDepthRangeM { get; init; }
}
