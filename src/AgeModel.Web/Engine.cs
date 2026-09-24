using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeModel.Web;

public sealed class AgeModelEngine
{
    public DerivationResult Evaluate(CasePackage package, IReadOnlyList<CaseEvent> events, string branchId, int candidateBudget = 12)
    {
        var working = ClonePackage(package);
        var states = new List<(CaseEvent Event, RuleSet Rules, CasePackage Package, string? ScenarioCode, Dictionary<string,bool> Adopted, Dictionary<string,bool> Rejected, List<FrozenSnapshot> Frozen)>();
        var rules = working.InitialRuleSet;
        var scenarioCode = (string?)null;
        var adopted = new Dictionary<string, bool>();
        var rejected = new Dictionary<string, bool>();
        var frozen = new List<FrozenSnapshot>();
        var branchEvents = events.Where(e => e.BranchId == branchId).OrderBy(e => e.Sequence).ToList();

        foreach (var evt in branchEvents)
        {
            switch (evt.Type)
            {
                case "import":
                    rules = working.InitialRuleSet;
                    scenarioCode = evt.Payload.TryGetProperty("scenarioCode", out var sc) ? sc.GetString() : null;
                    break;
                case "branch":
                    scenarioCode = evt.Payload.TryGetProperty("scenarioCode", out var bsc) ? bsc.GetString() : scenarioCode;
                    break;
                case "parameter-revision":
                    rules = ApplyRuleRevision(rules, evt.Payload);
                    break;
                case "source-data-changed":
                    ApplySourceChange(working, evt);
                    break;
                case "human-decision":
                    ApplyHumanDecision(working, evt, adopted, rejected);
                    break;
                case "freeze-model":
                case "model-frozen":
                    frozen.Add(CreateFrozenSnapshot(working, rules, evt, frozen, candidateBudget));
                    break;
            }
            states.Add((evt, rules, ClonePackage(working), scenarioCode, new(adopted), new(rejected), frozen.Select(f => f with { }).ToList()));
        }

        var import = branchEvents.FirstOrDefault(e => e.Type is "import" or "branch") ?? branchEvents.FirstOrDefault();
        if (import is null)
            return EmptyFailure(package.CaseId, branchId, "NO_EVENTS", "No replay events were supplied");
        var last = states[^1];
        return BuildResult(last.Package, branchId, last.Event, last.Rules, last.ScenarioCode, last.Adopted, last.Rejected, last.Frozen, candidateBudget);
    }

    public string StableHash(object value) => StableHash(JsonSerializer.Serialize(value, AgeJson.Options));
    public static string StableHash(string canonical) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static RuleSet ApplyRuleRevision(RuleSet rules, JsonElement payload)
    {
        return new RuleSet
        {
            RuleVersion = payload.TryGetProperty("ruleVersion", out var rv) ? rv.GetString() ?? rules.RuleVersion : rules.RuleVersion,
            LowerTailProbability = payload.TryGetProperty("lowerTailProbability", out var lo) && lo.ValueKind == JsonValueKind.Number ? lo.GetDouble() : rules.LowerTailProbability,
            UpperTailProbability = payload.TryGetProperty("upperTailProbability", out var hi) && hi.ValueKind == JsonValueKind.Number ? hi.GetDouble() : rules.UpperTailProbability,
            MaxCandidates = payload.TryGetProperty("maxCandidates", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : rules.MaxCandidates,
            MonotonicBasis = rules.MonotonicBasis,
            DistributionBasis = rules.DistributionBasis
        };
    }

    private static void ApplySourceChange(CasePackage pkg, CaseEvent evt)
    {
        var id = evt.Payload.GetProperty("directSourceId").GetString()!;
        var kind = evt.Payload.TryGetProperty("sourceKind", out var k) ? k.GetString() : null;
        var field = evt.Payload.TryGetProperty("field", out var f) ? f.GetString() : null;
        var value = evt.Payload.TryGetProperty("newValue", out var nv) ? nv : default;
        if (kind == "anchor")
        {
            var anchor = pkg.Anchors.FirstOrDefault(a => a.Id == id);
            if (anchor is not null) pkg.Anchors[pkg.Anchors.IndexOf(anchor)] = MutateAnchor(anchor, field, value, evt.EventId);
        }
        else if (kind == "segment")
        {
            var segment = pkg.Segments.FirstOrDefault(s => s.Id == id);
            if (segment is not null) pkg.Segments[pkg.Segments.IndexOf(segment)] = MutateSegment(segment, field, value);
        }
        else if (kind == "event-layer")
        {
            var layer = pkg.EventLayers.FirstOrDefault(l => l.Id == id);
            if (layer is not null) pkg.EventLayers[pkg.EventLayers.IndexOf(layer)] = MutateEventLayer(layer, field, value);
        }
        else if (kind == "reservoir-offset")
        {
            var offset = pkg.ReservoirOffsets.FirstOrDefault(r => r.Id == id);
            if (offset is not null) pkg.ReservoirOffsets[pkg.ReservoirOffsets.IndexOf(offset)] = MutateOffset(offset, field, value);
        }
    }

    private static AgeAnchor MutateAnchor(AgeAnchor anchor, string? field, JsonElement value, string eventId)
    {
        var depth = anchor.Depth;
        var age = anchor.RawAge;
        var eventLayerId = anchor.EventLayerId;
        var reservoirId = anchor.ReservoirOffsetId;
        var addedBy = anchor.AddedByEventId ?? eventId;

        if (field == "depth" && value.ValueKind == JsonValueKind.Object)
        {
            var q = Quantity.Parse(value, "depth");
            depth = q;
        }
        if (field == "ageDistribution" && value.ValueKind == JsonValueKind.Object)
        {
            var samples = value.TryGetProperty("samples", out var ss)
                ? ss.EnumerateArray().Select(x => x.GetDouble()).ToArray()
                : age.Samples.ToArray();
            var unit = value.TryGetProperty("unit", out var un) ? un.GetString() ?? age.Unit : age.Unit;
            var basis = value.TryGetProperty("basis", out var bs) ? bs.GetString() ?? age.Basis : age.Basis;
            age = new AgeDistribution(age.Id, samples, unit, basis, age.RawText, age.ReservoirOffsetId);
        }
        if (field == "eventLayerId") eventLayerId = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (field == "reservoirOffsetId") reservoirId = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return anchor with { Depth = depth, RawAge = age, EventLayerId = eventLayerId, ReservoirOffsetId = reservoirId, AddedByEventId = addedBy };
    }

    private static CoreSegment MutateSegment(CoreSegment segment, string? field, JsonElement value)
    {
        var top = segment.Top;
        var bottom = segment.Bottom;
        var kind = segment.Kind;
        if (field == "top") top = Quantity.Parse(value, "depth");
        if (field == "bottom") bottom = Quantity.Parse(value, "depth");
        if (field == "kind" && value.ValueKind == JsonValueKind.String && Enum.TryParse<SegmentKind>(value.GetString(), true, out var parsed)) kind = parsed;
        return segment with { Top = top, Bottom = bottom, Kind = kind };
    }

    private static EventLayer MutateEventLayer(EventLayer layer, string? field, JsonElement value)
    {
        if (field == "topDepth") return layer with { TopDepth = Quantity.Parse(value, "depth") };
        if (field == "thickness") return layer with { Thickness = Quantity.Parse(value, "thickness") };
        if (field == "correlationKey" && value.ValueKind == JsonValueKind.String) return layer with { CorrelationKey = value.GetString()! };
        return layer;
    }

    private static ReservoirOffset MutateOffset(ReservoirOffset offset, string? field, JsonElement value)
    {
        if (field == "offset") return offset with { Offset = Quantity.Parse(value, "age") };
        if (field == "uncertainty") return offset with { Uncertainty = Quantity.Parse(value, "age") };
        return offset;
    }

    private static void ApplyHumanDecision(CasePackage pkg, CaseEvent evt, Dictionary<string,bool> adopted, Dictionary<string,bool> rejected)
    {
        var decisionId = evt.Payload.TryGetProperty("decisionId", out var di) ? di.GetString() : null;
        var action = evt.Payload.TryGetProperty("action", out var ac) ? ac.GetString() : null;
        var rationale = evt.Payload.TryGetProperty("rationale", out var ra) ? ra.GetString() : "";
        var subjectType = evt.Payload.TryGetProperty("subjectType", out var st) ? st.GetString() ?? "candidate" : "candidate";
        var subjectId = evt.Payload.TryGetProperty("subjectId", out var si) ? si.GetString() : null;
        if (decisionId is null || subjectId is null) return;

        if (subjectType == "anchor")
        {
            var existing = pkg.ExclusionDecisions.FirstOrDefault(d => d.SubjectId == subjectId && d.SubjectType == "anchor");
            var excluded = action == "exclude";
            var decision = new ExclusionDecision(decisionId, "anchor", subjectId, excluded, rationale ?? "", "reviewer", evt.CreatedAt, evt.EventId, "human-decision");
            if (existing is not null) pkg.ExclusionDecisions.Remove(existing);
            pkg.ExclusionDecisions.Add(decision);
        }
        else if (subjectType == "candidate")
        {
            if (action == "adopt") { adopted[subjectId] = true; rejected.Remove(subjectId); }
            if (action == "reject") { rejected[subjectId] = true; adopted.Remove(subjectId); }
        }
    }

    private static FrozenSnapshot CreateFrozenSnapshot(CasePackage package, RuleSet rules, CaseEvent evt, List<FrozenSnapshot> existing, int budget)
    {
        var candidateId = evt.Payload.TryGetProperty("candidateId", out var c) ? c.GetString() : null;
        var snapshot = BuildResult(package, evt.BranchId, evt, rules, null, new(), new(), existing, budget);
        var candidate = snapshot.Candidates.FirstOrDefault(c => c.Id == candidateId)
            ?? snapshot.Candidates.FirstOrDefault(c => c.Status == CandidateStatus.Valid)
            ?? snapshot.Candidates.FirstOrDefault();
        return new FrozenSnapshot(evt.EventId, candidate?.Id ?? "unresolved", candidate?.Label ?? "Unresolved", evt.EventId, candidate!, evt.ParentDependencies.ToList(), rules.RuleVersion);
    }

    private static DerivationResult BuildResult(CasePackage package, string branchId, CaseEvent evt, RuleSet rules, string? scenarioCode,
        Dictionary<string,bool> adopted, Dictionary<string,bool> rejected, List<FrozenSnapshot> frozen, int candidateBudget)
    {
        var diagnostics = new List<Diagnostic>();
        var judgments = new List<Judgment>();
        var segments = BuildSegments(package, diagnostics, judgments, evt.EventId);
        var anchors = BuildAnchors(package, diagnostics, judgments, evt.EventId).OrderBy(a => a, AnchorComparer.Instance).ToList();
        var excludedAnchorIds = package.ExclusionDecisions.Where(d => d.SubjectType == "anchor" && d.Excluded).Select(d => d.SubjectId).ToHashSet();
        var usable = anchors.Where(a => a.DepthM is not null && a.CorrectedQuantiles is not null && !excludedAnchorIds.Contains(a.Id)).ToList();

        DetectDuplicateAndTies(usable, package, judgments, evt.EventId, scenarioCode);
        DetectLayerCorrelation(anchors, package, judgments, evt.EventId);
        DetectUnconverted(package, judgments, evt.EventId);

        var generated = new List<Candidate>();
        var lineageSpecs = new (string LineageId, string Label, Func<IReadOnlyList<AnchorState>, IOrderedEnumerable<AnchorState>> Selector)[]
        {
            ("LINEAGE_EVENT", "Event-layer constrained", a => a.OrderBy(x => x.DepthM).ThenByDescending(x => x.EventLayerId is not null).ThenBy(x => x.Id, StringComparer.Ordinal)),
            ("LINEAGE_RADIOMETRIC", "Radiometric continuous", a => a.OrderBy(x => x.DepthM).ThenBy(x => x.CorrectedQuantiles!.Q50).ThenBy(x => x.Id, StringComparer.Ordinal)),
            ("LINEAGE_ROBUST", "Robust quantile envelope", a => a.OrderBy(x => x.DepthM).ThenBy(x => x.CorrectedQuantiles!.Q95).ThenBy(x => x.Id, StringComparer.Ordinal)),
        };

        foreach (var spec in lineageSpecs)
        {
            var ordered = spec.Selector(usable).ToList();
            generated.Add(BuildCandidate(spec.LineageId, spec.Label, ordered, segments, rules, package, evt.EventId, scenarioCode, false));
            var tieGroups = ordered.GroupBy(a => a.DepthM!.Value.ToString("G17")).Where(g => g.Count() > 1).ToList();
            if (tieGroups.Count > 0)
            {
                var chosen = ordered.GroupBy(a => a.DepthM!.Value).SelectMany(g => g.Count() > 1 ? g.OrderBy(x => x.Id, StringComparer.Ordinal).Take(1) : g).ToList();
                generated.Add(BuildCandidate(spec.LineageId + "_TIE_ALT", spec.Label + " — tied-depth alternate", chosen, segments, rules, package, evt.EventId, scenarioCode, true));
            }
        }

        var valid = generated.Where(c => c.Status == CandidateStatus.Valid).OrderByDescending(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var bestScore = valid.Count == 0 ? int.MinValue : valid[0].Score;
        var tieGroup = 0;
        foreach (var candidate in valid.Where(c => c.Score == bestScore))
        {
            var index = generated.IndexOf(candidate);
            generated[index] = candidate with { TieGroup = "TIE-" + (tieGroup + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) };
        }
        if (valid.Count(c => c.Score == bestScore) > 1)
            judgments.Add(new Judgment("JUDGMENT_EQUAL_OPTIMAL_" + evt.EventId, "EQUAL-OPTIMAL-MODELS", Severity.Warning, JudgmentStatus.Active,
                "Multiple equally defensible models are retained; the mean candidate was not selected.", "multi-candidate-retention", evt.EventId,
                valid.Where(c => c.Score == bestScore).Select(c => c.Id).ToArray(), null, scenarioCode));

        var status = DerivationStatus.Ok;
        if (generated.Count > Math.Max(1, Math.Min(rules.MaxCandidates, candidateBudget)))
        {
            status = DerivationStatus.ResourceBudgetExhausted;
            diagnostics.Add(new Diagnostic("$.maxCandidates", "RESOURCE_BUDGET_EXHAUSTED", $"Candidate budget {Math.Min(rules.MaxCandidates, candidateBudget)} was exhausted by {generated.Count} defensible variants.", Severity.Error));
        }
        if (generated.All(c => c.Status != CandidateStatus.Valid))
        {
            status = DerivationStatus.SemanticNoSolution;
            diagnostics.Add(new Diagnostic("$.candidates", "SEMANTIC_NO_SOLUTION", "No legal age-depth model satisfies the active segment and monotonic constraints.", Severity.Error));
        }

        foreach (var id in adopted.Keys)
        {
            var candidate = generated.FirstOrDefault(c => c.Id == id);
            if (candidate is not null) generated[generated.IndexOf(candidate)] = candidate with { Status = CandidateStatus.Frozen };
        }

        var frozenImpacts = CalculateFrozenImpacts(frozen, anchors, segments, rules, package, evt.EventId);
        var conflicting = adopted.Keys.Intersect(rejected.Keys).Any();
        if (conflicting) judgments.Add(new Judgment("JUDGMENT_CONFLICT_" + evt.EventId, "CONFLICTING-HUMAN-DECISIONS", Severity.Error, JudgmentStatus.Conflict,
            "The same candidate is both accepted and rejected; both decisions are retained.", "decision-conflict", evt.EventId, adopted.Keys.Intersect(rejected.Keys).ToArray(), null, scenarioCode));

        var display = BuildDisplayValues(package, segments, anchors, generated, frozen, frozenImpacts, evt.EventId);
        var summary = StableHash(new { package.CaseId, package.Segments.Count, package.Anchors.Count, package.ReservoirOffsets.Count, package.EventLayers.Count, package.ExclusionDecisions.Count, rules.RuleVersion, Parent = string.Join("|", evt.ParentDependencies) });
        return new DerivationResult(status, package.CaseId, branchId, evt.EventId, evt.Sequence, rules.RuleVersion, summary, evt.ParentDependencies,
            diagnostics, judgments, segments, anchors, generated, display, frozen, frozenImpacts, adopted.Keys.ToList(), rejected.Keys.ToList(), conflicting);
    }

    private sealed class AnchorComparer : IComparer<AnchorState>
    {
        public static AnchorComparer Instance { get; } = new();
        public int Compare(AnchorState? x, AnchorState? y) =>
            Comparer<double?>.Default.Compare(x?.DepthM, y?.DepthM) is var d && d != 0 ? d
            : string.CompareOrdinal(x?.Id, y?.Id);
    }

    private static List<SegmentState> BuildSegments(CasePackage package, List<Diagnostic> diagnostics, List<Judgment> judgments, string eventId)
    {
        var result = new List<SegmentState>();
        foreach (var segment in package.Segments.OrderBy(s => s.Top.NormalizedValue).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            double? top = segment.Top.NormalizedValue;
            double? bottom = segment.Bottom.NormalizedValue;
            string? failure = null;
            var valid = true;
            if (segment.Top.ConversionOk == false || segment.Bottom.ConversionOk == false)
            {
                valid = false;
                failure = segment.Top.ConversionDiagnostic ?? segment.Bottom.ConversionDiagnostic;
                diagnostics.Add(new Diagnostic($"$.segments[{segment.Id}]", "UNCONVERTED_DOMAIN_VALUE", failure!, Severity.Warning));
            }
            else if (top.HasValue && bottom.HasValue && top.Value >= bottom.Value)
            {
                if (segment.Kind == SegmentKind.Inverted)
                {
                    failure = "Inverted segment is explicit and is not used as a monotonic age boundary.";
                    judgments.Add(J("JUDGMENT_INVERTED_" + segment.Id + "_" + eventId, "INVERTED-SEGMENT", Severity.Warning, JudgmentStatus.Active, failure, "inverted-boundary", eventId, new[] { segment.Id }));
                }
                else
                {
                    valid = false;
                    failure = $"Core geometry is inverted: top {top} m must be shallower than bottom {bottom} m.";
                    judgments.Add(J("JUDGMENT_INVERTED_" + segment.Id + "_" + eventId, "INVERTED-SEGMENT", Severity.Error, JudgmentStatus.Active, failure, "invalid-core-geometry", eventId, new[] { segment.Id }));
                    diagnostics.Add(new Diagnostic($"$.segments[{segment.Id}]", "INVERTED_SEGMENT", failure, Severity.Error));
                }
            }
            if (segment.Kind is SegmentKind.Gap or SegmentKind.Unconformity)
            {
                judgments.Add(J("JUDGMENT_BOUNDARY_" + segment.Id + "_" + eventId, segment.Kind == SegmentKind.Unconformity ? "UNCONFORMITY-BOUNDARY" : "DEPTH-GAP-BOUNDARY",
                    Severity.Info, JudgmentStatus.Active,
                    segment.Kind + " is an explicit boundary; ages are not interpolated or required to be monotonic across it.",
                    "explicit-stratigraphic-boundary", eventId, new[] { segment.Id }));
            }
            result.Add(new SegmentState(segment.Id, segment.Label, segment.Kind, top, bottom, "m", valid, failure));
        }
        return result;
    }

    private static List<AnchorState> BuildAnchors(CasePackage package, List<Diagnostic> diagnostics, List<Judgment> judgments, string eventId)
    {
        var result = new List<AnchorState>();
        foreach (var anchor in package.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var anchorJudgments = new List<Judgment>();
            if (!anchor.Depth.ConversionOk)
            {
                diagnostics.Add(new Diagnostic($"$.anchors[{anchor.Id}].depth", "UNCONVERTED_DEPTH", anchor.Depth.ConversionDiagnostic ?? "Depth cannot be converted", Severity.Warning));
                anchorJudgments.Add(J("JUDGMENT_UNCONVERTED_DEPTH_" + anchor.Id + "_" + eventId, "UNCONVERTED-VALUE-RETAINED", Severity.Warning, JudgmentStatus.Active,
                    "Depth remains in its raw position and is excluded from plotting; no default value was invented.", "raw-representation-retained", eventId, new[] { anchor.Id }));
            }
            if (string.IsNullOrWhiteSpace(anchor.RawAge.Unit))
            {
                diagnostics.Add(new Diagnostic($"$.anchors[{anchor.Id}].ageDistribution", "MISSING_AGE_UNIT", "Age distribution is missing its time unit.", Severity.Error));
            }
            var raw = Statistics.Quantiles(anchor.RawAge.Samples);
            var corrected = CorrectDistribution(anchor, package, out var correctionDiagnostic);
            if (correctionDiagnostic is not null)
            {
                diagnostics.Add(new Diagnostic($"$.anchors[{anchor.Id}].ageDistribution", correctionDiagnostic.Code, correctionDiagnostic.Message, Severity.Warning));
                anchorJudgments.Add(J("JUDGMENT_RAW_DISTRIBUTION_" + anchor.Id + "_" + eventId, "DISTRIBUTION-RETAINED", Severity.Warning, JudgmentStatus.Active,
                    correctionDiagnostic.Message, "distribution-not-mean", eventId, new[] { anchor.Id }));
            }
            else
            {
                anchorJudgments.Add(J("JUDGMENT_DISTRIBUTION_" + anchor.Id + "_" + eventId, "DISTRIBUTION-RETAINED", Severity.Info, JudgmentStatus.Active,
                    $"Full distribution with {raw.SampleCount} samples is retained; displayed q05/q50/q95 are derived views.", "distribution-not-mean", eventId, new[] { anchor.Id }));
            }
            var exclusion = package.ExclusionDecisions.Where(d => d.SubjectType == "anchor" && d.SubjectId == anchor.Id).OrderByDescending(d => d.DecidedAt).ThenBy(d => d.Id, StringComparer.Ordinal).FirstOrDefault();
            if (exclusion?.Excluded == true)
            {
                anchorJudgments.Add(J("JUDGMENT_EXCLUSION_" + exclusion.Id + "_" + eventId, "EXCLUSION-DECISION-RETAINED", Severity.Info, JudgmentStatus.Excluded,
                    "Anchor excluded by a retained decision: " + exclusion.Reason, exclusion.Basis, eventId, new[] { anchor.Id }, exclusion.SourcePointer));
            }
            result.Add(new AnchorState(anchor.Id, anchor.Label, anchor.Depth.NormalizedValue,
                anchor.Depth.RawText ?? FormatQuantity(anchor.Depth), raw, corrected, corrected is null ? anchor.RawAge.Unit : "a BP",
                anchor.RawAge.Basis, exclusion?.Excluded == true, exclusion?.Reason, anchor.ReservoirOffsetId, anchor.EventLayerId, anchorJudgments));
        }
        return result;
    }

    private static Quantiles? CorrectDistribution(AgeAnchor anchor, CasePackage package, out Diagnostic? diagnostic)
    {
        diagnostic = null;
        var raw = Statistics.Quantiles(anchor.SamplesForCorrection());
        if (anchor.ReservoirOffsetId is null)
        {
            if (anchor.RawAge.Unit.Contains("14C"))
            {
                diagnostic = new Diagnostic($"$.anchors[{anchor.Id}]", "UNRESOLVED_RESERVOIR_OFFSET", "14C age has no reservoir offset; raw distribution retained without calendar normalization.", Severity.Warning);
                return raw;
            }
            return anchor.RawAge.Unit == "ka BP" ? Scale(raw, 1000) : raw;
        }
        var offset = package.ReservoirOffsets.FirstOrDefault(r => r.Id == anchor.ReservoirOffsetId);
        if (offset is null)
        {
            diagnostic = new Diagnostic($"$.anchors[{anchor.Id}]", "MISSING_RESERVOIR_OFFSET", "Reservoir offset reference cannot be resolved; raw distribution retained.", Severity.Warning);
            return raw;
        }
        if (offset.Offset.NormalizedValue is null)
        {
            diagnostic = new Diagnostic($"$.anchors[{anchor.Id}]", "UNCONVERTED_RESERVOIR_OFFSET", "Reservoir offset cannot be normalized; raw age distribution retained.", Severity.Warning);
            return raw;
        }
        var unc = offset.Offset.NormalizedValue ?? 0;
        return new Quantiles(raw.Q05 - unc, raw.Q50 - unc, raw.Q95 - unc, raw.Minimum - unc, raw.Maximum - unc, raw.SampleCount);
    }

    private static void DetectDuplicateAndTies(List<AnchorState> anchors, CasePackage package, List<Judgment> judgments, string eventId, string? scenario)
    {
        foreach (var group in anchors.GroupBy(a => a.DepthM!.Value).Where(g => g.Count() > 1))
        {
            var ids = group.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => a.Id).ToArray();
            var byAge = group.OrderBy(a => a.CorrectedQuantiles!.Q50).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            var overlap = byAge.Zip(byAge.Skip(1), (a, b) => a.CorrectedQuantiles!.Q95 >= b.CorrectedQuantiles!.Q05).Any(x => x);
            if (overlap)
                judgments.Add(J("JUDGMENT_TIED_" + string.Join("_", ids) + "_" + eventId, "TIED-ANCHORS", Severity.Warning, JudgmentStatus.Active,
                    "Anchors share depth and their uncertainty envelopes overlap; tied alternatives remain legal candidates.", "tied-anchor-retention", eventId, ids, null, scenario));
            else
                judgments.Add(J("JUDGMENT_DUPLICATE_" + string.Join("_", ids) + "_" + eventId, "DUPLICATE-DEPTH", Severity.Error, JudgmentStatus.Conflict,
                    "Repeated depths have non-overlapping ages and cannot represent one monotonic point without an explicit reviewer decision.", "duplicate-depth-no-silent-merge", eventId, ids, null, scenario));
        }
    }

    private static void DetectLayerCorrelation(List<AnchorState> anchors, CasePackage package, List<Judgment> judgments, string eventId)
    {
        foreach (var anchor in anchors.Where(a => a.EventLayerId is not null))
        {
            var layer = package.EventLayers.FirstOrDefault(l => l.Id == anchor.EventLayerId);
            if (layer is null)
            {
                judgments.Add(J("JUDGMENT_LAYER_MISSING_" + anchor.Id + "_" + eventId, "EVENT-LAYER-UNRESOLVED", Severity.Warning, JudgmentStatus.Conflict,
                    "Event-layer correlation points to missing evidence; correlation is retained but cannot constrain models.", "event-correlation-evidence", eventId, new[] { anchor.Id }));
                continue;
            }
            var status = layer.Confidence.Equals("rejected", StringComparison.OrdinalIgnoreCase) ? JudgmentStatus.Rejected : JudgmentStatus.Active;
            var severity = status == JudgmentStatus.Rejected ? Severity.Warning : Severity.Info;
            judgments.Add(J("JUDGMENT_LAYER_" + anchor.Id + "_" + eventId, "EVENT-LAYER-CORRELATED", severity, status,
                $"Anchor correlated to {layer.CorrelationKey} with confidence {layer.Confidence}.", "event-correlation-evidence", eventId, new[] { anchor.Id, layer.Id }));
        }
    }

    private static void DetectUnconverted(CasePackage package, List<Judgment> judgments, string eventId)
    {
        foreach (var anchor in package.Anchors.Where(a => !a.Depth.ConversionOk))
            judgments.Add(J("JUDGMENT_RAW_RETAINED_" + anchor.Id + "_" + eventId, "RAW-DOMAIN-VALUE-RETAINED", Severity.Warning, JudgmentStatus.Active,
                "An unconvertible domain value remains in the source record and is not plotted or replaced.", "no-value-dropped-for-plot", eventId, new[] { anchor.Id }));
    }

    private static Candidate BuildCandidate(string lineageId, string label, IReadOnlyList<AnchorState> anchors, List<SegmentState> segments,
        RuleSet rules, CasePackage package, string eventId, string? scenario, bool alternate)
    {
        var rates = new List<RateInterval>();
        var reasons = new List<string>();
        var curve = new List<Point>();
        var bands = new List<BandPoint>();
        var validCandidate = true;

        if (anchors.Count == 0)
        {
            return new Candidate(StableCandidateId(lineageId, anchors, eventId), lineageId, label, CandidateStatus.Invalid, 0, "", rates,
                Array.Empty<string>(), curve, bands, "No usable anchors remain after retained exclusions and conversion diagnostics.", false, null);
        }

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            curve.Add(new Point(anchor.DepthM!.Value, anchor.CorrectedQuantiles!.Q50, "a BP"));
            bands.Add(new BandPoint(anchor.DepthM.Value, anchor.CorrectedQuantiles.Q05, anchor.CorrectedQuantiles.Q50, anchor.CorrectedQuantiles.Q95, "a BP"));
            if (i == 0) continue;
            var previous = anchors[i - 1];
            var segment = SegmentBetween(segments, previous.DepthM!.Value, anchor.DepthM!.Value);
            var rateId = $"RATE::{lineageId}::{previous.Id}->{anchor.Id}";
            var localKey = $"interval:{previous.Id}:{anchor.Id}";
            if (segment is null)
            {
                validCandidate = false;
                reasons.Add($"No segment spans {previous.Id} to {anchor.Id}.");
                rates.Add(new RateInterval(rateId, "unresolved", previous.Id, anchor.Id, new DepthInterval(previous.DepthM, anchor.DepthM, "m", false, "No segment"), null, null, null, "a/m", false, "No segment spans the interval", localKey, StableHash(rateId)));
                continue;
            }
            if (segment.Kind is SegmentKind.Gap or SegmentKind.Unconformity)
            {
                var reason = segment.Kind + " explicitly breaks interpolation and monotonic transfer.";
                rates.Add(new RateInterval(rateId, segment.Id, previous.Id, anchor.Id,
                    new DepthInterval(previous.DepthM, anchor.DepthM, "m", true, null), null, null, null, "a/m", false, reason, localKey, StableHash(rateId)));
                continue;
            }
            if (segment.Kind == SegmentKind.Inverted || !segment.Valid)
            {
                validCandidate = false;
                var reason = segment.FailureReason ?? "Inverted segment breaks continuous core geometry.";
                reasons.Add(reason);
                rates.Add(new RateInterval(rateId, segment.Id, previous.Id, anchor.Id,
                    new DepthInterval(previous.DepthM, anchor.DepthM, "m", segment.Valid, reason), null, null, null, "a/m", false, reason, localKey, StableHash(rateId)));
                continue;
            }

            var depthSpan = anchor.DepthM.Value - previous.DepthM.Value;
            if (depthSpan <= 0)
            {
                validCandidate = false;
                var reason = "Duplicate or inverted control depths make a finite depositional rate undefined.";
                reasons.Add(reason);
                rates.Add(new RateInterval(rateId, segment.Id, previous.Id, anchor.Id,
                    new DepthInterval(previous.DepthM, anchor.DepthM, "m", false, reason), null, null, null, "a/m", false, reason, localKey, StableHash(rateId)));
                continue;
            }
            var q = previous.CorrectedQuantiles!;
            var next = anchor.CorrectedQuantiles!;
            var minRate = (next.Q05 - q.Q95) / depthSpan;
            var medianRate = (next.Q50 - q.Q50) / depthSpan;
            var maxRate = (next.Q95 - q.Q05) / depthSpan;
            var monotonic = next.Q05 >= q.Q95 - 0.000001;
            if (!monotonic)
            {
                validCandidate = false;
                var reason = "Age is not non-decreasing downward within a continuous segment.";
                reasons.Add(reason);
                rates.Add(new RateInterval(rateId, segment.Id, previous.Id, anchor.Id,
                    new DepthInterval(previous.DepthM, anchor.DepthM, "m", true, null), minRate, medianRate, maxRate, "a/m", false, reason, localKey, StableHash(rateId)));
                continue;
            }
            rates.Add(new RateInterval(rateId, segment.Id, previous.Id, anchor.Id,
                new DepthInterval(previous.DepthM, anchor.DepthM, "m", true, null), minRate, medianRate, maxRate, "a/m", true, null, localKey, StableHash(rateId)));
        }

        var eventScore = anchors.Count(a => a.EventLayerId is not null) * 4;
        var validRateScore = rates.Count(r => r.Valid) * 3;
        var score = anchors.Count * 2 + validRateScore + eventScore - reasons.Count * 5;
        return new Candidate(StableCandidateId(lineageId, anchors, eventId), lineageId, label,
            validCandidate ? CandidateStatus.Valid : CandidateStatus.Invalid, score, "", anchors.Select(a => a.Id).ToArray(),
            curve, bands, validCandidate ? null : string.Join(" ", reasons.Distinct()), false, null);
    }

    private static SegmentState? SegmentBetween(IReadOnlyList<SegmentState> segments, double top, double bottom)
    {
        return segments.FirstOrDefault(s => s.TopM.HasValue && s.BottomM.HasValue && s.TopM <= top + 0.0000001 && s.BottomM + 0.0000001 >= bottom);
    }

    private static string StableCandidateId(string lineageId, IReadOnlyList<AnchorState> anchors, string eventId)
    {
        var basis = lineageId + "|" + string.Join(",", anchors.Select(a => a.Id)) + "|" + eventId;
        return "CAND::" + lineageId + "::" + StableHash(basis)[..12];
    }

    private static List<FrozenImpact> CalculateFrozenImpacts(List<FrozenSnapshot> frozen, List<AnchorState> anchors, List<SegmentState> segments,
        RuleSet rules, CasePackage package, string eventId)
    {
        var impacts = new List<FrozenImpact>();
        foreach (var snapshot in frozen)
        {
            var frozenKeys = snapshot.Candidate.Rates.Select(r => r.LocalDependencyKey).ToHashSet();
            var currentControls = anchors.Where(a => snapshot.Candidate.ControlIds.Contains(a.Id) || a.AddedByEventId == eventId).ToList();
            var current = BuildCandidate(snapshot.Candidate.LineageId, snapshot.Candidate.Label, anchors.OrderBy(a => a.DepthM).ThenBy(a => a.Id).Where(a => snapshot.Candidate.ControlIds.Contains(a.Id)).ToList(),
                segments, rules, package, eventId, null, false);
            var changed = current.Rates.Where(r => frozenKeys.Contains(r.LocalDependencyKey) &&
                (!snapshot.Candidate.Rates.Any(f => f.Id == r.Id && f.ValueHash == r.ValueHash))).Select(r => r.LocalDependencyKey).ToList();
            var unchanged = current.Rates.Where(r => frozenKeys.Contains(r.LocalDependencyKey)).Select(r => r.LocalDependencyKey).Except(changed).ToList();
            var added = anchors.FirstOrDefault(a => a.AddedByEventId == eventId);
            string? conflict = null;
            if (added is not null && current.Rates.Any(r => !r.Valid && (r.FromAnchorId == added.Id || r.ToAnchorId == added.Id)))
                conflict = "NEW-ANCHOR-AFFECTS-FROZEN-LOCAL-INTERVAL";
            impacts.Add(new FrozenImpact(snapshot.Candidate.Id, added?.Id ?? "derivation", changed, unchanged, conflict));
        }
        return impacts;
    }

    private static List<DisplayValue> BuildDisplayValues(CasePackage package, IReadOnlyList<SegmentState> segments,
        IReadOnlyList<AnchorState> anchors, IReadOnlyList<Candidate> candidates, List<FrozenSnapshot> frozen,
        IReadOnlyList<FrozenImpact> impacts, string eventId)
    {
        var values = new List<DisplayValue>();
        foreach (var segment in segments)
        {
            values.Add(new DisplayValue($"DISP::{segment.Id}::top::{eventId}", segment.Label + " top", segment.Id, "segment-boundary", Format(segment.TopM), segment.TopM?.ToString("G17"), "m", package.DepthDatum, segment.Kind.ToString(),
                $"[{segment.TopM:G17},{segment.BottomM:G17}]", segment.FailureReason, eventId, StableHash(new { segment.Id, segment.TopM, segment.BottomM, eventId })));
        }
        foreach (var anchor in anchors)
        {
            values.Add(new DisplayValue($"DISP::{anchor.Id}::depth::{eventId}", anchor.Label + " depth", anchor.Id, "anchor-depth", Format(anchor.DepthM), anchor.DepthM?.ToString("G17"), "m", package.DepthDatum, "core depth",
                anchor.DepthM is null ? null : "[core surface, terminal depth]", anchor.Excluded ? anchor.ExclusionReason : null, eventId, StableHash(new { anchor.Id, anchor.DepthM, eventId })));
            if (anchor.CorrectedQuantiles is { } q)
            {
                values.Add(new DisplayValue($"DISP::{anchor.Id}::q05::{eventId}", anchor.Label + " young bound", anchor.Id, "uncertainty-band", Format(q.Q05), q.Q05.ToString("G17"), anchor.AgeUnit, package.TimeDatum, anchor.TimeBasis,
                    $"[q05={q.Q05:G17},q95={q.Q95:G17}]", anchor.ExclusionReason, eventId, StableHash(new { anchor.Id, "q05", q.Q05, eventId })));
                values.Add(new DisplayValue($"DISP::{anchor.Id}::median::{eventId}", anchor.Label + " median", anchor.Id, "anchor-distribution", Format(q.Q50), q.Q50.ToString("G17"), anchor.AgeUnit, package.TimeDatum, anchor.TimeBasis,
                    $"samples={q.SampleCount};range=[{q.Minimum:G17},{q.Maximum:G17}]", anchor.ExclusionReason, eventId, StableHash(new { anchor.Id, "median", q.Q50, eventId })));
                values.Add(new DisplayValue($"DISP::{anchor.Id}::q95::{eventId}", anchor.Label + " old bound", anchor.Id, "uncertainty-band", Format(q.Q95), q.Q95.ToString("G17"), anchor.AgeUnit, package.TimeDatum, anchor.TimeBasis,
                    $"[q05={q.Q05:G17},q95={q.Q95:G17}]", anchor.ExclusionReason, eventId, StableHash(new { anchor.Id, "q95", q.Q95, eventId })));
            }
        }
        foreach (var candidate in candidates)
        {
            foreach (var rate in candidate.Rates)
                values.Add(new DisplayValue($"DISP::{rate.Id}::{eventId}", rate.FromAnchorId + "→" + rate.ToAnchorId + " rate", candidate.Id, "rate-interval",
                    rate.Valid ? $"{Format(rate.Median)} [{Format(rate.Minimum)}, {Format(rate.Maximum)}]" : "not estimable", rate.Median?.ToString("G17"), rate.Unit, package.TimeDatum + " / core depth", candidate.LineageId,
                    rate.DepthInterval.Top is null ? null : $"[{rate.DepthInterval.Top:G17},{rate.DepthInterval.Bottom:G17}] {rate.DepthInterval.Unit}", rate.FailureReason, eventId, rate.ValueHash + ":" + eventId));
        }
        foreach (var impact in impacts)
            values.Add(new DisplayValue($"DISP::freeze-impact::{impact.FrozenCandidateId}::{eventId}", "Frozen model local impact", impact.FrozenCandidateId, "frozen-impact",
                $"changed={impact.ChangedLocalDependencyKeys.Count};unchanged={impact.UnchangedDependencyKeys.Count}", null, "count", null, "local dependency comparison", null, impact.NewConflictCode, eventId,
                StableHash(new { impact.FrozenCandidateId, impact.NewAnchorId, string.Join("|", impact.ChangedLocalDependencyKeys), eventId })));
        return values;
    }

    private static string? Format(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    private static string FormatQuantity(Quantity q) => q.RawText ?? (q.Value is null ? q.Unit : $"{q.Value:G17} {q.Unit}");
    private static Quantiles Scale(Quantiles q, double factor) => new(q.Q05 * factor, q.Q50 * factor, q.Q95 * factor, q.Minimum * factor, q.Maximum * factor, q.SampleCount);
    private static Judgment J(string id, string code, Severity severity, JudgmentStatus status, string message, string basis, string eventId, string[] affected, string? source = null, string? scenario = null)
        => new(id, code, severity, status, message, basis, eventId, affected, source, scenario);

    private static DerivationResult EmptyFailure(string caseId, string branchId, string code, string message)
    {
        return new DerivationResult(DerivationStatus.SemanticNoSolution, caseId, branchId, "", 0, "", "", Array.Empty<string>(),
            new[] { new Diagnostic("$", code, message, Severity.Error) }, Array.Empty<Judgment>(), Array.Empty<SegmentState>(),
            Array.Empty<AnchorState>(), Array.Empty<Candidate>(), Array.Empty<DisplayValue>(), Array.Empty<FrozenSnapshot>(),
            Array.Empty<FrozenImpact>(), Array.Empty<string>(), Array.Empty<string>(), false);
    }

    public static CasePackage ClonePackage(CasePackage package)
    {
        var json = JsonSerializer.Serialize(package, AgeJson.Options);
        return JsonSerializer.Deserialize<CasePackage>(json, AgeJson.Options)!;
    }
}

public static class AnchorExtensions
{
    public static IReadOnlyList<double> SamplesForCorrection(this AgeAnchor anchor)
    {
        if (anchor.RawAge.Unit == "ka BP") return anchor.RawAge.Samples.Select(s => s * 1000d).ToArray();
        return anchor.RawAge.Samples;
    }
}
