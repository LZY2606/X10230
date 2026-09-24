using System.Text.Json;

namespace AgeModel.Core;

public sealed record CommandResult(
    bool Accepted,
    string? ConflictCode,
    EventEnvelope? Event,
    EngineResult? Before,
    EngineResult? After,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string>? AffectedIds = null);

public sealed record CounterProbe(
    string Id,
    string Kind,
    string Label,
    string ChangedDirectSourceId,
    JsonElement InputFragment,
    IReadOnlySet<string> ExpectedAffectedIds);

public sealed class AgeModelService
{
    private readonly IEventStore store;
    private readonly ChronologyEngine engine = new();

    public AgeModelService(IEventStore store) => this.store = store;

    public ProjectState LoadState(string projectId, string? atEventId = null, string? branch = null)
    {
        var events = store.List(projectId);
        if (atEventId is not null)
        {
            var stop = events.FirstOrDefault(e => e.EventId == atEventId)
                ?? throw new InvalidOperationException($"Event {atEventId} does not exist.");
            events = Ancestors(events, stop).OrderBy(e => e.Sequence).ToList();
        }
        else if (branch is not null)
        {
            events = events.Where(e => e.Branch == branch || BranchAncestors(events, branch).Contains(e.EventId)).ToList();
            events = events.OrderBy(e => e.Sequence).ToList();
        }

        var imported = events.FirstOrDefault(e => e.EventType == "case.imported")
            ?? throw new InvalidOperationException("Project does not contain an imported case.");
        var package = JsonSerializer.Deserialize<CasePackage>(imported.PayloadJson, ChronologyJson.Options)!;
        var project = ProjectStateFactory.Initial(projectId, package, imported);
        var log = new List<EventEnvelope> { imported };
        foreach (var evt in events.Skip(1))
        {
            project = Apply(project, evt, log);
            log.Add(evt);
        }
        return project;
    }

    public EngineResult Evaluate(ProjectState state, int? budgetOverride = null, string? probeId = null) =>
        engine.Evaluate(state, budgetOverride, probeId);

    public EventEnvelope ImportCase(string projectId, string originalJson, DateTimeOffset now)
    {
        var import = CaseImporter.Import(originalJson);
        if (!import.Succeeded || import.Package is null)
            throw new ArgumentException(JsonSerializer.Serialize(import.Diagnostics, ChronologyJson.Options));
        var payload = JsonSerializer.Serialize(import.Package, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "case.imported", "main", Array.Empty<string>(),
            import.Package.Parameters.Version, payload, now);
        store.Append(evt);
        return evt;
    }

    public CommandResult ReviseParameters(string projectId, RuleParameters parameters, DateTimeOffset now)
    {
        var state = LoadState(projectId);
        var before = engine.Evaluate(state);
        var payload = JsonSerializer.Serialize(parameters, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "parameters.revised", state.Branch,
            state.EventsLog.TakeLast(1).Select(e => e.EventId).ToArray(), parameters.Version, payload, now);
        store.Append(evt);
        var after = engine.Evaluate(LoadState(projectId));
        return new CommandResult(true, null, evt, before, after, Array.Empty<Diagnostic>());
    }

    public CommandResult PatchSource(string projectId, string sourceId, JsonElement replacementAnchorFragment, DateTimeOffset now)
    {
        var state = LoadState(projectId);
        if (!state.Anchors.ContainsKey(sourceId))
            return Fail("SOURCE_NOT_FOUND", $"Direct source anchor {sourceId} does not exist.");
        var before = engine.Evaluate(state);
        var payload = JsonSerializer.Serialize(new { sourceId, replacement = replacementAnchorFragment }, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "source.patched", state.Branch,
            state.EventsLog.TakeLast(1).Select(e => e.EventId).ToArray(), state.Parameters.Version, payload, now);
        store.Append(evt);
        var afterState = LoadState(projectId);
        var after = engine.Evaluate(afterState);
        var affected = ComputeAffectedIds(before, after, sourceId);
        return new CommandResult(true, null, evt, before, after, after.Diagnostics, affected);
    }

    public CommandResult RecordDecision(string projectId, ExclusionDecision decision, DateTimeOffset now)
    {
        var state = LoadState(projectId);
        var conflicting = state.Exclusions.Values
            .FirstOrDefault(e => e.SubjectType == decision.SubjectType && e.SubjectId == decision.SubjectId &&
                                 e.Status != decision.Status);
        var before = engine.Evaluate(state);
        var payload = JsonSerializer.Serialize(decision, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "decision.recorded", state.Branch,
            state.EventsLog.TakeLast(1).Select(e => e.EventId).ToArray(), decision.AtRuleVersion, payload, now);
        store.Append(evt);
        var after = engine.Evaluate(LoadState(projectId));
        return new CommandResult(conflicting is null, conflicting is null ? null : "CONFLICTING_HUMAN_DECISION",
            evt, before, after, after.Diagnostics);
    }

    public CommandResult SetEventCorrelation(string projectId, string eventId, string status, string decidedBy, string reason, DateTimeOffset now)
    {
        var state = LoadState(projectId);
        if (!state.Events.ContainsKey(eventId))
            return Fail("EVENT_NOT_FOUND", $"Event layer {eventId} does not exist.");
        var before = engine.Evaluate(state);
        var payload = JsonSerializer.Serialize(new { eventId, status, decidedBy, reason }, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "correlation.decided", state.Branch,
            state.EventsLog.TakeLast(1).Select(e => e.EventId).ToArray(), state.Parameters.Version, payload, now);
        store.Append(evt);
        var after = engine.Evaluate(LoadState(projectId));
        return new CommandResult(true, null, evt, before, after, after.Diagnostics);
    }

    public CommandResult Freeze(string projectId, string candidateId, DateTimeOffset now)
    {
        var state = LoadState(projectId);
        if (state.FrozenCandidateId is not null)
            return new CommandResult(false, "ALREADY_FROZEN", null, engine.Evaluate(state), null,
                new[] { new Diagnostic("CONCURRENT_FROZEN", "error", "model.frozen", candidateId,
                    "A frozen candidate already exists; create a branch before comparing new evidence.") });
        var before = engine.Evaluate(state);
        if (!before.Candidates.Any(c => c.Id == candidateId))
            return Fail("CANDIDATE_NOT_FOUND", $"Candidate {candidateId} is not a legal current candidate.");
        var payload = JsonSerializer.Serialize(new { candidateId, frozenAt = now.ToString("O") }, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "model.frozen", state.Branch,
            state.EventsLog.TakeLast(1).Select(e => e.EventId).ToArray(), state.Parameters.Version, payload, now);
        store.Append(evt);
        return new CommandResult(true, null, evt, before, before, Array.Empty<Diagnostic>());
    }

    public CommandResult CompareNewAnchorOnFrozen(string projectId, AgeAnchor anchor, DateTimeOffset now, string? branchName = null)
    {
        var state = LoadState(projectId);
        if (state.FrozenCandidateId is null)
            return Fail("NOT_FROZEN", "Freeze a candidate before creating an evidence comparison branch.");
        var before = engine.Evaluate(state);
        var branch = branchName ?? "trial-" + anchor.Id;
        var payload = JsonSerializer.Serialize(anchor, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "trial.anchor-proposed", branch,
            new[] { state.FrozenEventId! }, state.Parameters.Version, payload, now, parentBranch: state.Branch);
        store.Append(evt);
        var trialState = LoadState(projectId, branch: branch);
        var after = engine.Evaluate(trialState);
        return new CommandResult(true, null, evt, before, after, after.Diagnostics,
            ComputeAffectedIds(before, after, anchor.Id));
    }

    public ProbeArchiveEntry RunCounterProbe(string projectId, CounterProbe probe, DateTimeOffset now, int? budgetOverride = null)
    {
        var baselineState = LoadState(projectId);
        var baseline = engine.Evaluate(baselineState, budgetOverride, probe.Id);
        var baselineHash = baseline.MaterialHash;
        var fragment = probe.InputFragment.GetRawText();
        var patched = PatchStateForProbe(baselineState, probe, fragment, out var diagnostics);
        var result = engine.Evaluate(patched, budgetOverride, probe.Id);
        diagnostics = diagnostics.Concat(result.Diagnostics).ToList();
        var actual = ComputeAffectedIds(baseline, result, probe.ChangedDirectSourceId);
        var expected = probe.ExpectedAffectedIds.ToArray();
        var contained = expected.OrderBy(x => x).SequenceEqual(actual.OrderBy(x => x));
        var adopted = result.Status == ProjectStatus.Ok && contained;
        var judgment = result.Status switch
        {
            ProjectStatus.ResourceBudgetExhausted => "RESOURCE_BUDGET_EXHAUSTED",
            ProjectStatus.SemanticallyUnsolvable => "SEMANTIC_NO_SOLUTION",
            ProjectStatus.FormatCorrupt => "FORMAT_CORRUPT",
            _ => contained ? "IMPACT_CONTAINED" : "IMPACT_ESCAPED_LOCAL_SCOPE"
        };
        var entry = new ProbeArchiveEntry(
            $"probe-{probe.Id}",
            probe.Kind,
            probe.Label,
            baselineState.EventsLog[^1].EventId,
            baselineHash,
            probe.ChangedDirectSourceId,
            adopted,
            judgment,
            adopted ? "Only dependent objects in the same local scope changed." : string.Join("; ", diagnostics.Select(d => d.Message)),
            expected,
            actual,
            contained,
            fragment,
            result.Candidates.Select(c => c.Id).ToArray(),
            result.RejectedModels.Select(r => r.Id).ToArray(),
            diagnostics,
            baselineState.Parameters.Version,
            baselineState.EventsLog[^1].EventId);
        var payload = JsonSerializer.Serialize(entry, ChronologyJson.Options);
        var evt = CreateEvent(projectId, "counterexample.archived", baselineState.Branch,
            new[] { baselineState.EventsLog[^1].EventId }, baselineState.Parameters.Version, payload, now);
        store.Append(evt);
        return entry;
    }
