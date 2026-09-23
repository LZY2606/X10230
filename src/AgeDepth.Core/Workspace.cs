using System.Text.Json;

namespace AgeDepth.Core;

/// <summary>
/// Orchestrates store + model builder. All business rules live here (server side),
/// so the web layer and tests call the same code paths.
/// </summary>
public sealed class Workspace
{
    private static readonly JsonSerializerOptions Json = CasePackData.JsonOptions;
    public Store Store { get; }

    public Workspace(Store store) => Store = store;

    public IReadOnlyList<string> ExcludedAnchorIds()
    {
        var excluded = new List<string>();
        foreach (var e in Store.Events())
        {
            if (e.Type != "decision.exclude-anchor") continue;
            using var doc = JsonDocument.Parse(e.Payload);
            if (doc.RootElement.TryGetProperty("anchorId", out var id))
                excluded.Add(id.GetString()!);
        }
        return excluded;
    }

    public ModelParameters CurrentParameters()
    {
        ModelParameters? latest = null;
        foreach (var e in Store.Events())
        {
            if (e.Type is not ("case.parameters" or "decision.param-revision")) continue;
            using var doc = JsonDocument.Parse(e.Payload);
            latest = JsonSerializer.Deserialize<ModelParameters>(doc.RootElement.GetProperty("parameters").GetRawText(), Json);
        }
        return latest ?? new ModelParameters("v1", 400, 2000, 0.5);
    }

    public ModelInput LoadInput(ModelParameters? parametersOverride = null, IReadOnlyList<AgeAnchor>? extraAnchors = null)
    {
        var excluded = ExcludedAnchorIds().ToHashSet(StringComparer.Ordinal);
        var sections = Store.Materials("section")
            .Select(m => JsonSerializer.Deserialize<Section>(m.Payload, Json)!).ToList();
        var boundaries = Store.Materials("boundary")
            .Select(m => JsonSerializer.Deserialize<Boundary>(m.Payload, Json)!).ToList();
        var anchors = Store.Materials("anchor")
            .Select(m => JsonSerializer.Deserialize<AgeAnchor>(m.Payload, Json)!)
            .Where(a => !excluded.Contains(a.Id))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.Last()) // latest version of an anchor id wins
            .ToList();
        if (extraAnchors is not null) anchors.AddRange(extraAnchors);
        var events = Store.Materials("event-layer")
            .Select(m => JsonSerializer.Deserialize<EventLayer>(m.Payload, Json)!).ToList();
        return new ModelInput(sections, boundaries, anchors, events, parametersOverride ?? CurrentParameters());
    }

    public ModelRunResult? LatestRun()
    {
        var last = Store.Runs().LastOrDefault(r => r.Status == "complete");
        if (last.Id is null) return null;
        var payload = Store.RunPayload(last.Id);
        return payload is null ? null : JsonSerializer.Deserialize<ModelRunResult>(payload, Json);
    }

    /// <summary>Rebuild from current state; records input digest, parameter version and parent run.</summary>
    public Result<ModelRunResult> Rebuild(ModelParameters? parametersOverride = null,
        IReadOnlyList<AgeAnchor>? extraAnchors = null, bool persist = true, string? trigger = null)
    {
        var previous = LatestRun();
        var input = LoadInput(parametersOverride, extraAnchors);
        var result = ModelBuilder.Build(input, previous);
        if (!result.IsOk || result.Value is null)
            return result;
        var run = result.Value;
        if (persist)
        {
            Store.SaveRun(run, JsonSerializer.Serialize(run, Json));
            var affected = run.Candidates
                .SelectMany(c => c.Segments.Where(s => !s.Reused).Select(s => $"{c.Kind}:{s.Segment.Id}"))
                .ToArray();
            var reused = run.Candidates
                .SelectMany(c => c.Segments.Where(s => s.Reused).Select(s => $"{c.Kind}:{s.Segment.Id}"))
                .ToArray();
            Store.AppendEvent("run.recompute", JsonSerializer.Serialize(new
            {
                runId = run.RunId,
                parentRunId = run.ParentRunId,
                paramVersion = run.ParamVersion,
                inputDigest = run.InputDigest,
                trigger = trigger ?? "manual",
                affectedSegments = affected,
                reusedSegments = reused,
                diagnostics = run.Diagnostics.Select(d => new { d.Code, d.Message, d.Location, d.Severity }).ToArray()
            }, Json), run.ParentRunId);
        }
        return result;
    }

    public Result<(string EventId, ModelRunResult Run)> AddAnchor(AgeAnchor anchor)
    {
        Store.UpsertMaterial("anchor", anchor.Id, JsonSerializer.Serialize(anchor, Json));
        Store.AppendEvent("decision.anchor-added", JsonSerializer.Serialize(new
        {
            anchorId = anchor.Id, anchor.Depth, anchor.Datum, unit = anchor.Distribution.Unit
        }, Json));
        var rebuilt = Rebuild(trigger: $"anchor-added:{anchor.Id}");
        if (!rebuilt.IsOk || rebuilt.Value is null)
            return Result.Failure<(string, ModelRunResult)>(rebuilt.Kind, rebuilt.Diagnostics.ToArray());
        return Result.Success(("decision.anchor-added", rebuilt.Value));
    }

    public string ExcludeAnchor(string anchorId, string reason)
    {
        var (_, id) = Store.AppendEvent("decision.exclude-anchor", JsonSerializer.Serialize(new
        {
            anchorId, reason, rule = "exclusion-decisions-preserved"
        }, Json));
        Rebuild(trigger: $"anchor-excluded:{anchorId}");
        return id;
    }

    public Result<ModelRunResult> ReviseParameters(ModelParameters next, string reason)
    {
        Store.AppendEvent("decision.param-revision", JsonSerializer.Serialize(new
        {
            parameters = next, reason
        }, Json));
        return Rebuild(parametersOverride: next, trigger: "param-revision");
    }

    public Result<string> Freeze(string modelId, int expectedVersion)
    {
        if (!Store.TrySetFrozen(modelId, expectedVersion, true))
        {
            var (version, _) = Store.ModelState(modelId);
            return Result.Failure<string>(ResultKind.Conflict,
                new Diagnostic("freeze.conflict",
                    $"Model {modelId} is at version {version}; expected {expectedVersion}. Another freeze won the race.",
                    $"model:{modelId}"));
        }
        var (v, _) = Store.ModelState(modelId);
        var (_, id) = Store.AppendEvent("decision.freeze", JsonSerializer.Serialize(new
        {
            modelId, version = v, rule = "freeze-then-compare"
        }, Json));
        return Result.Success(id);
    }

    public string DecideCandidate(string candidateId, bool accept, string reviewer, string rationale)
    {
        Store.SetCandidateStatus(candidateId, accept ? "accepted" : "rejected");
        var (_, id) = Store.AppendEvent(
            accept ? "decision.accept-candidate" : "decision.reject-candidate",
            JsonSerializer.Serialize(new { candidateId, reviewer, rationale, rule = "equal-candidates-retained" }, Json));
        return id;
    }

    /// <summary>Hypothetical comparison: add an anchor, rebuild in memory, never persisted.</summary>
    public Result<ModelRunResult> CompareWithNewAnchor(AgeAnchor hypothetical)
    {
        return Rebuild(extraAnchors: new[] { hypothetical }, persist: false, trigger: "compare-only");
    }

    /// <summary>Seed the case pack and replay the scripted session, including the conflict branch.</summary>
    public void SeedAndReplay(CasePackData pack)
    {
        foreach (var s in pack.Sections) Store.UpsertMaterial("section", s.Id, JsonSerializer.Serialize(s, Json));
        foreach (var b in pack.Boundaries) Store.UpsertMaterial("boundary", b.Id, JsonSerializer.Serialize(b, Json));
        foreach (var a in pack.Anchors) Store.UpsertMaterial("anchor", a.Id, JsonSerializer.Serialize(a, Json));
        foreach (var e in pack.EventLayers) Store.UpsertMaterial("event-layer", e.Id, JsonSerializer.Serialize(e, Json));
        foreach (var cx in pack.Counterexamples) Store.UpsertMaterial("counterexample", cx.Id, JsonSerializer.Serialize(cx, Json));

        Store.AppendEvent("case.import", JsonSerializer.Serialize(new
        {
            name = pack.Name,
            narrative = pack.Narrative,
            datum = CasePack.Datum,
            sections = pack.Sections.Count,
            boundaries = pack.Boundaries.Count,
            anchors = pack.Anchors.Count,
            eventLayers = pack.EventLayers.Count,
            parameters = pack.Parameters
        }, Json));

        foreach (var anchorId in pack.InitiallyExcludedAnchorIds)
        {
            Store.AppendEvent("decision.exclude-anchor", JsonSerializer.Serialize(new
            {
                anchorId, reason = "bulk-sediment replicate with hard-water effect; excluded at import review",
                rule = "exclusion-decisions-preserved"
            }, Json));
        }

        Store.AppendEvent("case.parameters", JsonSerializer.Serialize(new { parameters = pack.Parameters }, Json));

        // Baseline run.
        var baseline = Rebuild(trigger: "initial-import");
        AppendJudgments(baseline.Value);

        // 1) Parameter revision: iterations 400 -> 600 (param version v2).
        var revised = pack.Parameters with { ParamVersion = "v2", Iterations = 600 };
        ReviseParameters(revised, "reviewer requested tighter ensemble quantiles");

        // 2) Local source change: re-measured A5 (segment 2 only) supersedes the old version.
        var a5b = pack.Anchors.First(a => a.Id == "A5") with
        {
            Distribution = new AgeDistribution("normal", 9750, 120, null, null, null, null, null, "yr BP")
        };
        Store.UpsertMaterial("anchor", a5b.Id, JsonSerializer.Serialize(a5b, Json));
        Store.AppendEvent("decision.source-revision", JsonSerializer.Serialize(new
        {
            anchorId = a5b.Id, scope = "segment-local", note = "re-measured A5; only segment 2 must recompute"
        }, Json));
        Rebuild(trigger: "source-revision:A5");

        // 3) Conflicting human decisions: two reviewers accept different candidates; both kept.
        var latest = LatestRun();
        if (latest is not null)
        {
            var linear = latest.Candidates.First(c => c.Kind == CandidateKinds.PiecewiseLinear);
            var constant = latest.Candidates.First(c => c.Kind == CandidateKinds.ConstantRate);
            DecideCandidate(linear.CandidateId, true, "reviewer-1", "fidelity to anchor distributions");
            DecideCandidate(constant.CandidateId, true, "reviewer-2", "parsimonious rate history");
            Store.AppendEvent("decision.conflict", JsonSerializer.Serialize(new
            {
                candidates = new[] { linear.CandidateId, constant.CandidateId },
                resolution = "unresolved-both-retained",
                rule = "equal-candidates-retained"
            }, Json));
        }

        // Freeze the model so new anchors are compared against the frozen baseline.
        var (version, _) = Store.ModelState(CasePack.ModelId);
        Freeze(CasePack.ModelId, version);

        // Archive counterexample outcomes (adopted and rejected) as machine-readable events.
        foreach (var cx in pack.Counterexamples)
            ArchiveCounterexample(pack, cx);
    }

    private void AppendJudgments(ModelRunResult? run)
    {
        if (run is null) return;
        var rules = new[]
        {
            new { rule = "monotonic-within-continuous-segment", statement = "年龄只能在不断层段中单调", applied = true },
            new { rule = "explicit-boundaries", statement = "深度缺口和不整合是显式边界", applied = true },
            new { rule = "distribution-not-mean", statement = "一个锚点的分布不能被均值替代", applied = true },
            new { rule = "equal-candidates-retained", statement = "多个等优模型需保留", applied = true },
        };
        foreach (var rule in rules)
        {
            Store.AppendEvent("judgment", JsonSerializer.Serialize(new
            {
                rule.rule, rule.statement, rule.applied, runId = run.RunId
            }, Json), run.RunId);
        }
    }

    private void ArchiveCounterexample(CasePackData pack, CounterexampleScenario cx)
    {
        var excluded = pack.InitiallyExcludedAnchorIds.ToHashSet(StringComparer.Ordinal);
        var anchors = pack.Anchors.Where(a => !excluded.Contains(a.Id) && !cx.RemoveAnchorIds.Contains(a.Id))
            .GroupBy(a => a.Id).Select(g => g.Last()).ToList();
        foreach (var add in cx.AddAnchors)
        {
            anchors.RemoveAll(a => a.Id == add.Id);
            anchors.Add(add);
        }
        var sections = pack.Sections.Concat(cx.AddSections).ToList();
        var boundaries = pack.Boundaries.Concat(cx.AddBoundaries).ToList();
        var input = new ModelInput(sections, boundaries, anchors, pack.EventLayers, pack.Parameters);
        var result = ModelBuilder.Build(input);
        var codes = result.Diagnostics.Select(d => d.Code)
            .Concat(result.Value?.Diagnostics.Select(d => d.Code) ?? Enumerable.Empty<string>())
            .Distinct().ToArray();
        Store.AppendEvent("counterexample.outcome", JsonSerializer.Serialize(new
        {
            scenarioId = cx.Id,
            title = cx.Title,
            change = cx.Change,
            adopted = cx.Adopted,
            rejectedNote = cx.RejectedNote,
            resultKind = result.Kind.ToString(),
            diagnosticCodes = codes,
            expectedDiagnostics = cx.ExpectedDiagnostics,
            expectationsMet = cx.ExpectedDiagnostics.All(codes.Contains)
        }, Json));
    }
}
