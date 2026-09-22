using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoCore;

public sealed record DerivationRequest(
    ProjectMaterials Materials,
    RuleParameters Parameters,
    IReadOnlyDictionary<string, ExclusionDecision> Decisions,
    string EventId,
    string BranchId,
    IReadOnlyList<string> ParentDependencies,
    string InputSummary,
    IReadOnlyDictionary<string, string>? SourceRevisions = null,
    int CandidateBudget = 256,
    int DistributionSampleBudget = 50_000);

public sealed record ExclusionDecision(string TargetId, bool Excluded, string Rationale, string DecidedBy);

public sealed class AgeModelEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DerivationResult Derive(DerivationRequest request)
    {
        ValidateBaseMaterials(request.Materials);
        CheckBudgets(request);

        var diagnostics = new List<ModelDiagnostic>();
        var anchors = BuildAnchors(request, diagnostics).OrderBy(a => a.Depth).ThenBy(a => a.Id).ToArray();
        var tracts = BuildTracts(request.Materials, diagnostics).OrderBy(t => t.Order).ThenBy(t => t.Id).ToArray();
        DetectAnchorProblems(anchors, request.Parameters.TieDepthTolerance, diagnostics);

        var tieGroups = anchors
            .Where(a => a.Adopted && a.ExclusionReason is null && !a.Diagnostics.Any(d => d.Severity == "error"))
            .GroupBy(a => a.SourceObservationId)
            .Select(g => g.OrderBy(a => a.Id).ToArray())
            .Where(g => g.Length > 1)
            .ToArray();

        var choices = BuildTieChoices(tieGroups, request.Parameters.MaxTieSetChoices);
        var methods = new[] { "piecewise-linear", "monotone-pchip" };
        var candidates = new List<ModelCandidate>();
        var globalDiagnosticIds = diagnostics.Select(d => Stable($"{request.EventId}:diag:{d.Code}:{d.ObjectId}")).ToArray();

        foreach (var choice in choices)
        {
            foreach (var method in methods)
            {
                candidates.Add(BuildCandidate(method, choice, anchors, tracts, request, globalDiagnosticIds));
            }
        }

        if (candidates.Count > request.Parameters.MaxCandidates || candidates.Count > request.CandidateBudget)
        {
            throw new ResourceBudgetExhaustedException("age-model-candidates", candidates.Count, Math.Min(request.Parameters.MaxCandidates, request.CandidateBudget));
        }

        var ordered = candidates
            .OrderByDescending(c => c.Status == "valid")
            .ThenBy(c => c.Score)
            .ThenBy(c => c.StableId, StringComparer.Ordinal)
            .ToArray();

        var bestScore = ordered.Where(c => c.Status == "valid").Select(c => c.Score).DefaultIfEmpty(double.MaxValue).First();
        ordered = ordered.Select(c => c with { EquallyOptimal = c.Status == "valid" && Math.Abs(c.Score - bestScore) < 0.0000001d }).ToArray();
        var status = ordered.Any(c => c.Status == "valid")
            ? (diagnostics.Any(d => d.Severity == "error") ? "valid-with-warnings" : "valid")
            : "semantic-impossible";
        var resultId = Stable($"result:{request.EventId}:{request.InputSummary}");
        var assertionJson = BuildRuleAssertions(request, anchors, tracts, ordered);

        return new DerivationResult
        {
            StableId = resultId,
            EventId = request.EventId,
            BranchId = request.BranchId,
            Anchors = anchors,
            Tracts = tracts,
            Candidates = ordered,
            Diagnostics = diagnostics.OrderBy(d => d.Position).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray(),
            Status = status,
            InputSummary = request.InputSummary,
            ParameterVersion = request.Parameters.Version,
            ParentDependencies = request.ParentDependencies.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            RuleAssertionJson = assertionJson
        };
    }

    private static void ValidateBaseMaterials(ProjectMaterials materials)
    {
        if (string.IsNullOrWhiteSpace(materials.PackageId))
        {
            throw new DamagedPackageException("Package id is missing", "$.packageId");
        }
        if (materials.Sections.Count == 0)
        {
            throw new SemanticModelException("INCOMPLETE_SECTIONS", "At least one core section is required", materials.PackageId, "$.sections");
        }
        if (!materials.RadioAges.Any(r => !string.IsNullOrWhiteSpace(r.Distribution?.Id)))
        {
            throw new SemanticModelException("INCOMPLETE_ANCHORS", "At least one radiometric distribution is required", materials.PackageId, "$.radioAges");
        }
        foreach (var observation in materials.RadioAges)
        {
            if (!observation.DepthUnit.Equals(materials.DepthDatum.LengthUnit, StringComparison.OrdinalIgnoreCase))
            {
                throw new SemanticModelException("UNIT_CONVERSION_UNAVAILABLE", $"Depth unit {observation.DepthUnit} cannot be normalized to {materials.DepthDatum.LengthUnit}", observation.Id, $"$.radioAges[{observation.Id}].depthUnit");
            }
            if (observation.Distribution.Samples.Count == 0)
            {
                throw new SemanticModelException("INCOMPLETE_DISTRIBUTION", "An empirical distribution must retain samples instead of a mean", observation.Id, $"$.radioAges[{observation.Id}].distribution.samples");
            }
        }
    }

    private static void CheckBudgets(DerivationRequest request)
    {
        var samples = request.Materials.RadioAges.Sum(r => r.Distribution.Samples.Count);
        if (samples > request.DistributionSampleBudget)
        {
            throw new ResourceBudgetExhaustedException("distribution-samples", samples, request.DistributionSampleBudget);
        }
    }

    private static IReadOnlyList<AgeAnchor> BuildAnchors(DerivationRequest request, List<ModelDiagnostic> diagnostics)
    {
        var result = new List<AgeAnchor>();
        var exclusions = request.Materials.ExclusionDecisions.ToDictionary(e => e.TargetId);
        foreach (var observation in request.Materials.RadioAges)
        {
            var anchorDiagnostics = new List<ModelDiagnostic>();
            var reservoir = observation.ReservoirOffsetId is null
                ? null
                : request.Materials.ReservoirOffsets.FirstOrDefault(o => o.Id == observation.ReservoirOffsetId);
            if (observation.ReservoirOffsetId is not null && reservoir is null)
            {
                anchorDiagnostics.Add(Diag("MISSING_RESERVOIR_OFFSET", "Reservoir offset reference is missing", observation.Id, $"radioAges.{observation.Id}.reservoirOffsetId", "error"));
            }

            var rawDistribution = observation.Distribution;
            if (observation.Diagnostic is not null)
            {
                anchorDiagnostics.Add(Diag("RAW_VALUE_RETAINED", observation.Diagnostic, observation.Id, $"radioAges.{observation.Id}", "warning"));
            }

            var shiftedSamples = reservoir is null
                ? rawDistribution.Samples
                : rawDistribution.Samples.Select(s => s with { CalibratedAgeCalBP = s.CalibratedAgeCalBP + reservoir.DeltaRCalYear, RawRepresentation = $"{s.RawRepresentation} + ΔR {reservoir.DeltaRCalYear:R} a" }).ToArray();
            var distribution = rawDistribution with { Samples = shiftedSamples, RawRepresentation = rawDistribution.RawRepresentation, Diagnostic = rawDistribution.Diagnostic };
            var adoptedByRule = request.Decisions.TryGetValue(observation.Id, out var decision)
                ? !decision.Excluded
                : exclusions.TryGetValue(observation.Id, out var material) && material.Excluded == false;
            var exclusionReason = request.Decisions.TryGetValue(observation.Id, out var d) && d.Excluded
                ? d.Rationale
                : exclusions.TryGetValue(observation.Id, out var e) && e.Excluded
                    ? e.Rationale
                    : null;

            result.Add(new AgeAnchor
            {
                Id = Stable($"anchor:{observation.Id}"),
                SourceObservationId = observation.Id,
                Depth = observation.Depth,
                DepthUnit = observation.DepthUnit,
                DatumId = observation.DatumId,
                Distribution = distribution,
                Adopted = adoptedByRule && !anchorDiagnostics.Any(x => x.Severity == "error"),
                ExclusionReason = exclusionReason,
                Diagnostics = anchorDiagnostics
            });
        }

        foreach (var exclusion in request.Materials.ExclusionDecisions)
        {
            if (!request.Materials.RadioAges.Any(r => r.Id == exclusion.TargetId) && exclusion.TargetType == "radioAge")
            {
                diagnostics.Add(Diag("EXCLUSION_TARGET_MISSING", "Exclusion decision is retained but its target is absent", exclusion.Id, $"exclusionDecisions.{exclusion.Id}", "warning"));
            }
        }

        diagnostics.AddRange(result.SelectMany(a => a.Diagnostics));
        return result;
    }

    private static IReadOnlyList<AgeTract> BuildTracts(ProjectMaterials materials, List<ModelDiagnostic> diagnostics)
    {
        var tracts = new List<AgeTract>();
        var boundaries = materials.Gaps
            .Select(g => new Boundary(g.Id, g.Kind, g.UpperDepth, g.LowerDepth, g.UpperDepth <= g.LowerDepth ? g.Evidence : "Gap interval is reversed"))
            .OrderBy(b => b.UpperDepth)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var section in materials.Sections.OrderBy(s => s.TopDepth).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (section.TopDepth >= section.BottomDepth)
            {
                diagnostics.Add(Diag("DUPLICATE_OR_REVERSED_SECTION_DEPTH", "Section depth interval is empty or reversed", section.Id, $"sections.{section.Id}", "error"));
            }

            var cuts = boundaries.Where(b => b.UpperDepth > section.TopDepth && b.LowerDepth < section.BottomDepth).ToArray();
            var start = section.TopDepth;
            var order = tracts.Count;
            foreach (var cut in cuts)
            {
                tracts.Add(Tract(section, start, cut.UpperDepth, order, cut.Id, cut.Kind, cut.Reason));
                start = cut.LowerDepth;
                order++;
            }

            var upper = boundaries.FirstOrDefault(b => Math.Abs(b.LowerDepth - section.TopDepth) < 0.000001d);
            var lower = boundaries.FirstOrDefault(b => Math.Abs(b.UpperDepth - section.BottomDepth) < 0.000001d);
            tracts.Add(Tract(section, start, section.BottomDepth, order, upper?.Id, lower?.Id, upper is null ? "continuous within core section" : $"{upper.Kind}: {upper.Reason}"));
        }

        foreach (var gap in materials.Gaps)
        {
            if (gap.UpperDepth >= gap.LowerDepth)
            {
                diagnostics.Add(Diag("DAMAGED_GAP_DEPTH", "Segment gap has duplicate or reversed depth", gap.Id, $"gaps.{gap.Id}", gap.Kind == "coring-gap" ? "error" : "warning"));
            }
            if (gap.Kind is not ("unconformity" or "coring-gap" or "conformable-contact"))
            {
                diagnostics.Add(Diag("UNKNOWN_BOUNDARY_KIND", "Gap kind is not a recognized explicit boundary", gap.Id, $"gaps.{gap.Id}.kind", "error"));
            }
        }

        return tracts.OrderBy(t => t.TopDepth).ThenBy(t => t.Id, StringComparer.Ordinal).Select((t, i) => t with { Order = i }).ToArray();
    }

    private static AgeTract Tract(CoreSection section, double top, double bottom, int order, string? upperId, string? lowerId, string reason) =>
        new()
        {
            Id = Stable($"tract:{section.Id}:{top:R}:{bottom:R}"),
            Order = order,
            TopDepth = top,
            BottomDepth = bottom,
            LengthUnit = section.LengthUnit,
            DatumId = section.DatumId,
            UpperBoundaryId = upperId,
            LowerBoundaryId = lowerId,
            BoundaryReason = reason
        };

    private record Boundary(string Id, string Kind, double UpperDepth, double LowerDepth, string Reason);

    private static void DetectAnchorProblems(IReadOnlyList<AgeAnchor> anchors, double tieTolerance, List<ModelDiagnostic> diagnostics)
    {
        for (var i = 0; i < anchors.Count; i++)
        {
            for (var j = i + 1; j < anchors.Count; j++)
            {
                var first = anchors[i];
                var second = anchors[j];
                if (Math.Abs(first.Depth - second.Depth) > tieTolerance)
                {
                    continue;
                }

                var firstObservation = first.SourceObservationId;
                var secondObservation = second.SourceObservationId;
                var samePhysicalAlternative = firstObservation == secondObservation;
                diagnostics.Add(samePhysicalAlternative
                    ? Diag("PARALLEL_ANCHOR", "Parallel anchors at the same depth are retained as distinct legal choices", firstObservation, $"radioAges.{firstObservation}", "warning")
                    : Diag("DUPLICATE_DEPTH", "Two distinct source observations claim the same depth", $"{firstObservation}|{secondObservation}", $"radioAges.{firstObservation}|{secondObservation}", "error"));
            }

            var tail = anchors[i].Distribution.Quantile(0.95) - anchors[i].Distribution.Quantile(0.05);
            var medianDistance = Math.Abs(anchors[i].Distribution.Quantile(0.5) - anchors[i].Distribution.Mean());
            if (tail > 0 && medianDistance / tail > 0.18d)
            {
                diagnostics.Add(Diag("LONG_TAIL_DISTRIBUTION", "Empirical distribution has a retained long tail and is not replaced by its mean", anchors[i].SourceObservationId, $"radioAges.{anchors[i].SourceObservationId}.distribution", "warning"));
            }
        }
    }

    private static IReadOnlyList<AgeAnchor[]> BuildTieChoices(IEnumerable<IGrouping<string, AgeAnchor>> tieGroups, int maxChoices)
    {
        var groups = tieGroups.Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray()).ToArray();
        var choices = new List<AgeAnchor[]> { Array.Empty<AgeAnchor>() };
        foreach (var group in groups)
        {
            var expanded = new List<AgeAnchor[]>();
            foreach (var choice in choices)
            {
                foreach (var option in group)
                {
                    expanded.Add(choice.Concat(new[] { option }).ToArray());
                }
            }

            if (expanded.Count > maxChoices)
            {
                throw new ResourceBudgetExhaustedException("tie-anchor-combinations", expanded.Count, maxChoices);
            }

            choices = expanded;
        }

        return choices;
    }

    private static ModelCandidate BuildCandidate(string method, IReadOnlyList<AgeAnchor> tieChoice, IReadOnlyList<AgeAnchor> anchors, IReadOnlyList<AgeTract> tracts, DerivationRequest request, IReadOnlyList<string> globalDiagnosticIds)
    {
        var tiedIds = tieChoice.ToDictionary(a => a.SourceObservationId, a => a.Id);
        var chosen = anchors
            .Where(a => a.Adopted && a.ExclusionReason is null && !a.Diagnostics.Any(d => d.Severity == "error"))
            .GroupBy(a => a.SourceObservationId)
            .Select(g => tiedIds.TryGetValue(g.Key, out var selected) ? selected : g.OrderBy(a => a.Id, StringComparer.Ordinal).First())
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToArray();

        var diagnostics = new List<ModelDiagnostic>();
        var curves = new List<CurvePoint>();
        var rates = new List<RateInterval>();
        var affected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tract in tracts)
        {
            var tractAnchors = chosen
                .Where(a => a.Depth >= tract.TopDepth - 0.000001d && a.Depth <= tract.BottomDepth + 0.000001d)
                .OrderBy(a => a.Depth)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToArray();
            if (tractAnchors.Length < 2)
            {
                diagnostics.Add(Diag("UNDERCONSTRAINED_TRACT", "Tract needs at least two anchors for a unique interpolation", tract.Id, $"tracts.{tract.Id}", "warning"));
                continue;
            }

            CheckMonotonicity(tract, tractAnchors, diagnostics, affected);
            var points = BuildCurvePoints(method, tract, tractAnchors, request.Parameters.MaxCurvePoints);
            curves.AddRange(points);
            rates.AddRange(BuildRates(tract, tractAnchors, diagnostics, affected));
        }

        var candidateId = Stable($"candidate:{method}:{string.Join("+", chosen.Select(a => a.Id))}");
        var status = diagnostics.Any(d => d.Severity == "error") ? "semantic-impossible" : "valid";
        var score = ComputeScore(chosen, curves, diagnostics);
        var dependencies = chosen.Select(a => a.SourceObservationId).Concat(tracts.SelectMany(t => new[] { t.UpperBoundaryId, t.LowerBoundaryId }.OfType<string>())).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToArray();
        affected.UnionWith(dependencies);

        return new ModelCandidate
        {
            StableId = candidateId,
            MethodId = method,
            DisplayName = $"{method} / {chosen.Length} anchors",
            AnchorIds = chosen.Select(a => a.Id).ToArray(),
            Tracts = tracts,
            Curve = curves.OrderBy(p => p.Depth).ThenBy(p => p.StableId, StringComparer.Ordinal).ToArray(),
            Rates = rates.OrderBy(r => r.TractId, StringComparer.Ordinal).ThenBy(r => r.StableId, StringComparer.Ordinal).ToArray(),
            Score = score,
            EquallyOptimal = false,
            Status = status,
            Diagnostics = diagnostics.OrderBy(d => d.Position).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray(),
            AffectedObjectIds = affected.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            InputSummary = request.InputSummary,
            ParameterVersion = request.Parameters.Version,
            ParentDependencies = request.ParentDependencies.Concat(dependencies).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToArray(),
            Adopted = false
        };
    }

    private static void CheckMonotonicity(AgeTract tract, IReadOnlyList<AgeAnchor> anchors, List<ModelDiagnostic> diagnostics, HashSet<string> affected)
    {
        for (var i = 1; i < anchors.Count; i++)
        {
            var upper = anchors[i - 1];
            var lower = anchors[i];
            affected.Add(upper.SourceObservationId);
            affected.Add(lower.SourceObservationId);
            affected.Add(tract.Id);
            if (lower.Depth < upper.Depth)
            {
                diagnostics.Add(Diag("INVERTED_SECTION", "Age-depth order is inverted inside an unbroken tract", tract.Id, $"tracts.{tract.Id}#{upper.SourceObservationId}->{lower.SourceObservationId}", "error"));
                continue;
            }

            var upperQ95 = upper.Distribution.Quantile(0.95);
            var lowerQ05 = lower.Distribution.Quantile(0.05);
            if (lowerQ05 < upperQ95 && lower.Distribution.Quantile(0.5) < upper.Distribution.Quantile(0.5))
            {
                diagnostics.Add(Diag("AGE_ORDER_INVERTED", "Younger median age appears down-section without an explicit boundary", tract.Id, $"tracts.{tract.Id}#{upper.SourceObservationId}->{lower.SourceObservationId}", "error"));
            }
            else if (lowerQ05 < upperQ95)
            {
                diagnostics.Add(Diag("UNCERTAINTY_OVERLAP", "Age distributions overlap; order is retained probabilistically instead of forcing equality", tract.Id, $"tracts.{tract.Id}#{upper.SourceObservationId}->{lower.SourceObservationId}", "warning"));
            }
        }
    }
}
