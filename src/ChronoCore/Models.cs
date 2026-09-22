using System.Text.Json.Serialization;

namespace ChronoCore;

public sealed record WeightedAgeSample(double CalibratedAgeCalBP, double Weight)
{
    public string RawRepresentation { get; init; } = $"{CalibratedAgeCalBP:R} cal BP";
}

public sealed record EmpiricalDistribution
{
    public required string Id { get; init; }
    public required IReadOnlyList<WeightedAgeSample> Samples { get; init; }
    public required string TimeUnit { get; init; }
    public required string CalendarBasis { get; init; }
    public string RawRepresentation { get; init; } = "";
    public string? Diagnostic { get; init; }

    public double Quantile(double probability)
    {
        if (Samples.Count == 0)
        {
            throw new SemanticModelException("EMPTY_DISTRIBUTION", $"Distribution {Id} contains no samples", Id);
        }

        var ordered = Samples.OrderBy(s => s.CalibratedAgeCalBP).ThenBy(s => s.Weight).ToArray();
        var total = ordered.Sum(s => s.Weight);
        if (total <= 0)
        {
            throw new SemanticModelException("NON_POSITIVE_WEIGHT", $"Distribution {Id} has non-positive total weight", Id);
        }

        var target = Math.Clamp(probability, 0d, 1d) * total;
        var cumulative = 0d;
        foreach (var sample in ordered)
        {
            cumulative += sample.Weight;
            if (target <= cumulative)
            {
                return sample.CalibratedAgeCalBP;
            }
        }

        return ordered[^1].CalibratedAgeCalBP;
    }

    public double Mean() => Samples.Sum(s => s.CalibratedAgeCalBP * s.Weight) / Samples.Sum(s => s.Weight);
}

public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Origin { get; init; }
    public required string PositiveDirection { get; init; }
    public required string LengthUnit { get; init; }
    public required string RawRepresentation { get; init; }
}

public sealed record CoreSection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double TopDepth { get; init; }
    public required double BottomDepth { get; init; }
    public required string LengthUnit { get; init; }
    public required string DatumId { get; init; }
    public required string Lithology { get; init; }
    public string DepositionStatus { get; init; } = "normal";
    public required string RawRepresentation { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed record SegmentGap
{
    public required string Id { get; init; }
    public required double UpperDepth { get; init; }
    public required double LowerDepth { get; init; }
    public required string LengthUnit { get; init; }
    public required string DatumId { get; init; }
    public required string Kind { get; init; }
    public required string Evidence { get; init; }
    public required string RawRepresentation { get; init; }
}

public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required double DeltaRCalYear { get; init; }
    public required double SigmaCalYear { get; init; }
    public required string Source { get; init; }
    public required string RawRepresentation { get; init; }
}

public sealed record RadioAgeObservation
{
    public required string Id { get; init; }
    public required double Depth { get; init; }
    public required string DepthUnit { get; init; }
    public required string DatumId { get; init; }
    public required string Method { get; init; }
    public required string Material { get; init; }
    public string? TieGroupId { get; init; }
    public required EmpiricalDistribution Distribution { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public required string RawRepresentation { get; init; }
    public string? Diagnostic { get; init; }
}

public sealed record EventBed
{
    public required string Id { get; init; }
    public required double Depth { get; init; }
    public required double Thickness { get; init; }
    public required string LengthUnit { get; init; }
    public required string DatumId { get; init; }
    public required string CorrelationKey { get; init; }
    public required string AgeBasis { get; init; }
    public required string RawRepresentation { get; init; }
}

public sealed record ExclusionDecisionMaterial
{
    public required string Id { get; init; }
    public required string TargetId { get; init; }
    public required string TargetType { get; init; }
    public required bool Excluded { get; init; }
    public required string Rationale { get; init; }
    public required string DecidedBy { get; init; }
    public required string RawRepresentation { get; init; }
}

public sealed record ProjectMaterials
{
    public required string SchemaVersion { get; init; }
    public required string PackageId { get; init; }
    public required string Title { get; init; }
    public required DepthDatum DepthDatum { get; init; }
    public required IReadOnlyList<CoreSection> Sections { get; init; }
    public required IReadOnlyList<SegmentGap> Gaps { get; init; }
    public required IReadOnlyList<ReservoirOffset> ReservoirOffsets { get; init; }
    public required IReadOnlyList<RadioAgeObservation> RadioAges { get; init; }
    public required IReadOnlyList<EventBed> EventBeds { get; init; }
    public required IReadOnlyList<ExclusionDecisionMaterial> ExclusionDecisions { get; init; }
}

public sealed record RuleParameters
{
    public required string Version { get; init; }
    public int MaxTieSetChoices { get; init; } = 8;
    public int MaxCandidates { get; init; } = 32;
    public int MaxCurvePoints { get; init; } = 240;
    public double TieDepthTolerance { get; init; } = 0.02;
    public bool EnforceMonotonicityAcrossBoundaries { get; init; }
    public string UpdatedBy { get; init; } = "sedimentology-team";
}

public sealed record ModelDiagnostic
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required string ObjectId { get; init; }
    public required string Position { get; init; }
    public required string Severity { get; init; }
}

public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string SourceObservationId { get; init; }
    public required double Depth { get; init; }
    public required string DepthUnit { get; init; }
    public required string DatumId { get; init; }
    public required EmpiricalDistribution Distribution { get; init; }
    public required bool Adopted { get; init; }
    public string? ExclusionReason { get; init; }
    public required IReadOnlyList<ModelDiagnostic> Diagnostics { get; init; }
}

public sealed record AgeTract
{
    public required string Id { get; init; }
    public required int Order { get; init; }
    public required double TopDepth { get; init; }
    public required double BottomDepth { get; init; }
    public required string LengthUnit { get; init; }
    public required string DatumId { get; init; }
    public string? UpperBoundaryId { get; init; }
    public string? LowerBoundaryId { get; init; }
    public required string BoundaryReason { get; init; }
}

public sealed record CurvePoint
{
    public required string StableId { get; init; }
    public required double Depth { get; init; }
    public required double MedianCalBP { get; init; }
    public required double Q05CalBP { get; init; }
    public required double Q95CalBP { get; init; }
}

public sealed record RateInterval
{
    public required string StableId { get; init; }
    public required string TractId { get; init; }
    public required double? MinRate { get; init; }
    public required double? MaxRate { get; init; }
    public required string Unit { get; init; }
    public required string EffectiveDepthRange { get; init; }
    public string? InvalidReason { get; init; }
}

public sealed record ModelCandidate
{
    public required string StableId { get; init; }
    public required string MethodId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
    public required IReadOnlyList<AgeTract> Tracts { get; init; }
    public required IReadOnlyList<CurvePoint> Curve { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required double Score { get; init; }
    public required bool EquallyOptimal { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<ModelDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<string> AffectedObjectIds { get; init; }
    public required string InputSummary { get; init; }
    public required string ParameterVersion { get; init; }
    public required IReadOnlyList<string> ParentDependencies { get; init; }
    public required bool Adopted { get; init; }
    public string? RejectionReason { get; init; }
}

public sealed record DerivationResult
{
    public required string StableId { get; init; }
    public required string EventId { get; init; }
    public required string BranchId { get; init; }
    public required IReadOnlyList<AgeAnchor> Anchors { get; init; }
    public required IReadOnlyList<AgeTract> Tracts { get; init; }
    public required IReadOnlyList<ModelCandidate> Candidates { get; init; }
    public required IReadOnlyList<ModelDiagnostic> Diagnostics { get; init; }
    public required string Status { get; init; }
    public required string InputSummary { get; init; }
    public required string ParameterVersion { get; init; }
    public required IReadOnlyList<string> ParentDependencies { get; init; }
    public required string RuleAssertionJson { get; init; }
}

public sealed record DisplayValue
{
    public required string StableId { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string UnitOrDatum { get; init; }
    public required string EffectiveRange { get; init; }
    public string? InvalidReason { get; init; }
    public required string SourceEventId { get; init; }
    public required IReadOnlyList<string> DependencyIds { get; init; }
}

public sealed record ProjectEvent
{
    public required string Id { get; init; }
    public required int Sequence { get; init; }
    public required string BranchId { get; init; }
    public required string Type { get; init; }
    public required string ParentEventId { get; init; }
    public required string ParameterVersion { get; init; }
    public required string InputSummary { get; init; }
    public required string PayloadJson { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? FrozenResultId { get; init; }
    public required string Status { get; init; }
}

public sealed record ExportBundle
{
    public required string SchemaVersion { get; init; }
    public required string ProjectId { get; init; }
    public required string ExportedAt { get; init; }
    public required IReadOnlyList<ProjectEvent> Events { get; init; }
}

public sealed record CounterexampleProbe
{
    public required string StableId { get; init; }
    public required string Name { get; init; }
    public required string ChangedDirectSourceId { get; init; }
    public required string BaselineEventId { get; init; }
    public required string ResultEventId { get; init; }
    public required string ExpectedImpactScope { get; init; }
    public required string Status { get; init; }
    public required bool Adopted { get; init; }
    public required IReadOnlyList<string> AffectedObjectIds { get; init; }
    public required IReadOnlyList<string> PreservedObjectIds { get; init; }
    public required string MachineRuleJson { get; init; }
}
