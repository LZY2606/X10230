namespace Chrono.Core;

/// <summary>
/// Application service. All business commands go through here so they are callable directly from
/// tests (no HTTP/browser needed). Every derived result records input digest, parameter version and
/// parent results; history is append-only and replayable.
/// </summary>
public sealed class ProjectService
{
    private readonly IProjectStore _store;
    private readonly Func<DateTime> _clock;

    public ProjectService(IProjectStore store, Func<DateTime>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string CreateProject(string projectId, string name, string caseId)
    {
        _store.CreateProject(projectId, name, caseId);
        var state = State(projectId, "main");
        Append(projectId, "main", 0, new ProjectCreatedPayload(name, caseId), "system", "", null);
        return projectId;
    }

    public CommandResult ImportMaterials(string projectId, string branchId, SourceBundle sources,
        string revisionId)
    {
        var parsed = SourceParser.Parse(sources, revisionId);
        if (parsed.Outcome == ImportOutcome.FormatCorrupt)
            throw new CommandRejectedException("FORMAT_CORRUPT",
                parsed.CorruptLocation ?? "unknown location");

        var state = State(projectId, branchId);
        var payload = new MaterialsPayload(parsed.Materials ??
            throw new CommandRejectedException("SEMANTIC_REJECTED", "materials semantically rejected"));
        var ev = Append(projectId, branchId, state.Version, payload, "reviewer",
            ModelingEngine.StableId("src", revisionId), null);
        return new CommandResult(ev, Projector.Project(branchId, StreamAfter(projectId, branchId, ev.Seq)), false);
    }

    /// <summary>Replace exactly one source document; only locally dependent anchors recompute.</summary>
    public CommandResult ReplaceOneSource(string projectId, string branchId,
        string documentName, SourceBundle rebuilt, string newRevisionId, IReadOnlySet<string> expectedAffectedIds)
    {
        var before = State(projectId, branchId);
        var parsed = SourceParser.Parse(rebuilt, newRevisionId);
        if (parsed.Outcome != ImportOutcome.Imported || parsed.Materials is null)
            throw new CommandRejectedException(parsed.Outcome == ImportOutcome.FormatCorrupt
                ? "FORMAT_CORRUPT" : "SEMANTIC_REJECTED",
                parsed.CorruptLocation ?? "semantic rejection while rebuilding " + documentName);

        var state = State(projectId, branchId);
        var ev = Append(projectId, branchId, state.Version,
            new SourceReplacedPayload(documentName, before.MaterialRevisionId ?? "", parsed.Materials),
            "reviewer", ModelingEngine.StableId("src", newRevisionId), null);

        var after = DeriveInternal(projectId, branchId,
            new[] { before.LatestDerivation?.ResultId }.Where(x => x is not null).ToArray()!,
            markLocalRecompute: expectedAffectedIds);
        return new CommandResult(ev, after.State, false);
    }

    public CommandResult ReviseParameters(string projectId, string branchId, ParameterSet parameters)
    {
        var state = State(projectId, branchId);
        var ev = Append(projectId, branchId, state.Version, new ParameterPayload(parameters),
            "reviewer", parameters.VersionId, null);
        return new CommandResult(ev, Projector.Project(branchId, StreamAfter(projectId, branchId, ev.Seq)), false);
    }

    public CommandResult Derive(string projectId, string branchId)
    {
        var state = State(projectId, branchId);
        var parents = state.Derivations.Count == 0
            ? Array.Empty<string>()
            : new[] { state.Derivations[^1].ResultId };
        var result = DeriveInternal(projectId, branchId, parents, markLocalRecompute: null);
        return new CommandResult(result.Event, result.State, false);
    }

    private (ChronoEvent Event, BranchState State) DeriveInternal(string projectId, string branchId,
        IReadOnlyList<string> parents, IReadOnlySet<string>? markLocalRecompute)
    {
        var state = State(projectId, branchId);
        if (state.Materials is null)
            throw new CommandRejectedException("NO_MATERIALS", "cannot derive before import");

        var engine = new ModelingEngine(state.Parameters);
        var result = engine.Derive(state.Materials,
            new DeriveContext(state.MaterialRevisionId!, parents));

        var findings = new List<Finding>(result.Findings);
        if (markLocalRecompute is not null && markLocalRecompute.Count > 0)
            findings.Add(new Finding(FindingKind.LocalRecompute,
                $"Only dependents of changed source recomputed: {string.Join(",", markLocalRecompute.OrderBy(x => x))}",
                markLocalRecompute.OrderBy(x => x).ToList(),
                new Dictionary<string, string>
                {
                    ["scope"] = "local",
                    ["parentResult"] = parents.FirstOrDefault() ?? "",
                    ["contained"] = "true"
                }));

        result = result with { Findings = findings };
        var ev = Append(projectId, branchId, state.Version, new DerivedPayload(result),
            "engine", result.InputDigest, state.Events.Count == 0 ? null : state.Events[^1].EventId);
        return (ev, Projector.Project(branchId, StreamAfter(projectId, branchId, ev.Seq)));
    }

    /// <summary>Accept/reject/adjust a candidate. After a freeze, conflicting decisions are kept with
    /// their conflict flag rather than silently overwriting the frozen choice.</summary>
    public CommandResult Decide(string projectId, string branchId, string candidateId,
        string action, string note)
    {
        var state = State(projectId, branchId);
        var latest = state.LatestDerivation
            ?? throw new CommandRejectedException("NO_DERIVATION", "nothing to decide on");
        if (latest.Candidates.All(c => c.Id != candidateId))
            throw new CommandRejectedException("UNKNOWN_CANDIDATE",
                $"candidate {candidateId} is not part of the current derivation");

        var sameTarget = state.Decisions.LastOrDefault(d => d.CandidateId == candidateId);
        var conflicts = state.Frozen is not null &&
                        state.Frozen.CandidateId != candidateId && action == "accept";
        var payload = new DecisionPayload(candidateId, action, note,
            sameTarget?.EventId, conflicts);
        var ev = Append(projectId, branchId, state.Version, payload, "reviewer",
            latest.InputDigest, state.Events[^1].EventId);
        return new(ev, Projector.Project(branchId, StreamAfter(projectId, branchId, ev.Seq)), conflicts);
    }

    /// <summary>Freeze a candidate model. A second concurrent freeze loses the version race and is
    /// recorded as a structured rejection (concurrent freeze), not a silent overwrite.</summary>
    public CommandResult Freeze(string projectId, string branchId, string candidateId, string note,
        int? expectedVersionOverride = null)
    {
        var state = State(projectId, branchId);
        if (state.Frozen is not null)
            throw new CommandRejectedException("ALREADY_FROZEN",
                $"branch already frozen on {state.Frozen.CandidateId} at {state.Frozen.EventId}");
        var expected = expectedVersionOverride ?? state.Version;
        var payload = new FrozenPayload(candidateId, note);
        var ev = TryOrReject(projectId, branchId, expected, payload, "freeze",
            $"concurrent freeze: version {state.Version} changed before commit");
        return new(ev, Projector.Project(branchId, StreamAfter(projectId, branchId, ev.Seq)), false);
    }

    /// <summary>Create a branch from a historical position; prior event versions are copied.</summary>
    public string Branch(string projectId, string newBranchId, string fromBranchId, int forkSeq, string reason)
    {
        _store.CreateBranch(projectId, newBranchId, fromBranchId, forkSeq, reason);
        var state = State(projectId, fromBranchId);
        var payload = new BranchPayload(newBranchId, fromBranchId, forkSeq, reason);
        // branch metadata event is appended to the *new* branch after its copied prefix
        var copied = State(projectId, newBranchId);
        var ev = _store.TryAppend(projectId, newBranchId, copied.Version, payload,
            "reviewer", "", copied.Events.Count == 0 ? null : copied.Events[^1].EventId,
            _clock().ToString("o"));
        if (ev is null) throw new CommandRejectedException("BRANCH_RACE", "branch creation raced");
        return newBranchId;
    }

    // ---------------------------------------------------------------- reads
    public BranchState State(string projectId, string branchId) =>
        Projector.Project(branchId, _store.ReadStream(projectId, branchId));

    /// <summary>Historical view: state exactly as it was immediately after event at index seq-1.</summary>
    public BranchState StateAt(string projectId, string branchId, int upToSeq)
    {
        var all = _store.ReadStream(projectId, branchId);
        return Projector.Project(branchId, all.Where(e => e.Seq < upToSeq));
    }

    public IReadOnlyList<BranchInfo> Branches(string projectId) => _store.ListBranches(projectId);

    private ChronoEvent Append(string projectId, string branchId, int expected,
        EventPayload payload, string author, string digest, string? parent)
    {
        var ev = _store.TryAppend(projectId, branchId, expected, payload, author, digest, parent,
            _clock().ToString("o"))
            ?? throw new CommandRejectedException("VERSION_CONFLICT",
                $"expected version {expected} but branch moved; reload and retry");
        return ev;
    }

    private ChronoEvent TryOrReject(string projectId, string branchId, int expected,
        EventPayload payload, string command, string reason)
    {
        var ev = _store.TryAppend(projectId, branchId, expected, payload, "reviewer", "", null,
            _clock().ToString("o"));
        if (ev is not null) return ev;

        // the losing concurrent command is itself preserved as evidence on the branch
        var state = State(projectId, branchId);
        var rejected = Append(projectId, branchId, state.Version,
            new RejectedPayload(command, reason, expected.ToString()), "system", "",
            state.Events.Count == 0 ? null : state.Events[^1].EventId);
        throw new CommandRejectedException("CONCURRENT_FREEZE", reason) { /* caller maps 409 */ };
    }

    private IReadOnlyList<ChronoEvent> StreamAfter(string projectId, string branchId, int seq) =>
        _store.ReadStream(projectId, branchId).Where(e => e.Seq <= seq).ToList();
}

