using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chrono.Core.Domain;
using Chrono.Core.Engine;
using Chrono.Core.Models;

namespace Chrono.Storage;

public sealed record ActionResult(bool Ok, OutcomeKind Kind, string? EventId, string? ConflictLastId,
    IReadOnlyList<Diagnostic> Diagnostics, AppState? State, string? BranchId);

/// <summary>
/// Application service: the single server-side entry point used by the web
/// layer, tests and the seeder. Business rules run through AgeModelEngine;
/// every derived result records an input digest, parameter version and parent
/// dependencies inside its immutable event.
/// </summary>
public sealed class SessionService
{
    private readonly IEventStore _store;
    private List<StoredEvent> _events = new();
    private readonly object _gate = new();
    public const string SystemAuthor = "case-seeder";

    public SessionService(IEventStore store) => _store = store;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _store.InitializeAsync(ct);
        _events = (await _store.LoadAllAsync(ct)).OrderBy(e => e.Seq).ToList();
    }

    public IReadOnlyList<StoredEvent> Events { get { lock (_gate) return _events.ToList(); } }

    public AppState State(string? upToEventId = null, string? branch = null)
    {
        lock (_gate) return Projector.Project(_events, upToEventId, branch);
    }

    public async Task<ActionResult> SeedFromImportAsync(ImportBatchPayload batch, string projectId, string name,
        ParameterSet parameters, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_events.Count > 0)
                return new ActionResult(true, OutcomeKind.Ok, null, null,
                    Array.Empty<Diagnostic>(), Projector.Project(_events), "main");
        }
        await Append(EventType.ProjectCreated, new ProjectCreatedPayload(projectId, name, parameters),
            "create project " + name, SystemAuthor, "main", ct: ct);
        return await Append(EventType.ImportBatch, batch, "initial domain case import", SystemAuthor, "main", ct: ct);
    }

    public Task<ActionResult> ReviseParametersAsync(ParameterSet p, string reason, CancellationToken ct = default)
        => AppendWithDerive(EventType.ParameterRevision, new ParameterRevisionPayload(p, reason, Array.Empty<string>()),
            "parameter revision: " + reason, "reviewer", "main", null, ct);

    public Task<ActionResult> ReviseSourceAsync(string anchorId, AgeAnchor revised,
        IReadOnlyList<string> localDeps, string reason, CancellationToken ct = default)
        => AppendWithDerive(EventType.SourceRevision,
            new SourceRevisionPayload(anchorId, revised, localDeps, reason),
            "source revision: " + reason, "geochemist", "main", localDeps, ct);

    public async Task<ActionResult> ExcludeAsync(string anchorId, string reason, string who, CancellationToken ct = default)
    {
        var excId = "exc-" + Guid.NewGuid().ToString("N")[..8];
        return await Append(EventType.AnchorExcluded,
            new AnchorExcludedPayload(anchorId, excId, reason), $"exclude {anchorId}: {reason}", who, "main", ct: ct);
    }

    public async Task<ActionResult> RestoreAsync(string anchorId, string reason, CancellationToken ct = default)
    {
        var s = State();
        var a = s.Anchors.GetValueOrDefault(anchorId);
        return await Append(EventType.AnchorRestored,
            new AnchorRestoredPayload(anchorId, a?.ExclusionId ?? "manual", reason),
            $"restore {anchorId}: {reason}", "reviewer", "main", ct: ct);
    }

    public async Task<ActionResult> CreateBranchAsync(string branchId, string name, bool scratch, string note, CancellationToken ct = default)
        => await Append(EventType.BranchCreated,
            new BranchCreatedPayload(branchId, name, "main", scratch, note),
            "branch " + note, "reviewer", "main", overrideBranchForEvent: "main", ct: ct);

    public async Task<ActionResult> ResolveConflictAsync(string decisionId, string a, string b,
        string chosenBranch, string note, IReadOnlyList<string> affected, CancellationToken ct = default)
        => await Append(EventType.DecisionConflict,
            new DecisionConflictPayload(decisionId, a, b, chosenBranch, note, affected),
            "conflict decision " + decisionId, "reviewer", "main", overrideBranchForEvent: "main", ct: ct);

    /// <summary>Freeze a candidate; any later attempt to mutate the frozen model
    /// on the same branch is rejected with a structured CONCURRENT_FROZEN judgment
    /// instead of silently rewriting history.</summary>
    public async Task<ActionResult> FreezeAsync(string candidateId, CancellationToken ct = default)
    {
        var s = State();
        if (s.FrozenCandidateId is not null && s.FrozenCandidateId != candidateId)
            return FrozenReject(s);
        if (!s.Candidates.ContainsKey(candidateId))
            return new ActionResult(false, OutcomeKind.SemanticImpossible, null, null,
                new[] { new Diagnostic("FREEZE-404", $"候选 {candidateId} 不存在", $"candidate:{candidateId}") }, s, "main");
        return await Append(EventType.ModelFrozen, new ModelFrozenPayload(candidateId),
            "freeze " + candidateId, "reviewer", "main", ct: ct);
    }

    public bool IsFrozen(string branch = "main") =>
        State(branch: branch).FrozenCandidateId is not null;

    /// <summary>Derive candidates on a branch (default main) and append one
    /// immutable ModelDerived event per candidate. Accepted AND rejected results
    /// are both retained.</summary>
    public async Task<ActionResult> DeriveAsync(string branch = "main",
        IReadOnlyCollection<string>? localOnly = null, CancellationToken ct = default)
    {
        var s = State(branch: branch);
        var engine = new AgeModelEngine();
        var result = engine.Derive(s, localOnly);

        foreach (var c in result.Candidates.Concat(result.Rejected))
        {
            var ev = await Append(EventType.ModelDerived, new ModelDerivedPayload(c),
                $"derive {c.Id} ({(c.Accepted ? "accepted" : "rejected")})", "engine", branch, derive: true, ct: ct);
            if (!ev.Ok) return ev;
        }
        foreach (var j in result.Judgments)
        {
            var ev = await Append(EventType.Judgment, new JudgmentPayload(j),
                "judgment " + j.Code, "engine", branch, derive: true, ct: ct);
            if (!ev.Ok) return ev;
        }
        var after = State(branch: branch);
        return new ActionResult(result.Kind == OutcomeKind.Ok, result.Kind,
            null, null, result.Diagnostics, after, branch);
    }

    public async Task<ActionResult> RecordProbeAsync(ProbeRecord probe, CancellationToken ct = default)
        => await Append(EventType.ProbeRecorded, new ProbePayload(probe),
            "counterexample probe " + probe.ProbeKind, "auditor", "main", derive: true, ct: ct);

    /// <summary>Add a hypothetical new anchor on a scratch branch for impact
    /// comparison against the frozen model, without touching the frozen trunk.</summary>
    public async Task<ActionResult> AddAnchorOnBranchAsync(string branch, AgeAnchor anchor, CancellationToken ct = default)
    {
        if (branch == "main" && IsFrozen("main"))
        {
            var s = State();
            return FrozenReject(s);
        }
        // Anchors added live are carried as an immutable import event on the branch.
        var branchState = State(branch: branch);
        var batch = new ImportBatchPayload(null, Array.Empty<CoreSegment>(),
            new[] { anchor }, Array.Empty<ReservoirCorrection>(), Array.Empty<EventLayer>(),
            Array.Empty<ExclusionDecision>(), anchor.Diagnostics, Array.Empty<Judgment>());
        return await Append(EventType.ImportBatch, batch, $"scratch anchor {anchor.Id}", "reviewer", branch, ct: ct);
    }

    private async Task<ActionResult> AppendWithDerive(EventType type, object payload,
        string fragment, string author, string branch, IReadOnlyCollection<string>? localOnly, CancellationToken ct)
    {
        if (branch == "main" && IsFrozen("main") && type != EventType.ModelFrozen)
        {
            var frozen = State();
            return FrozenReject(frozen);
        }
        var first = await Append(type, payload, fragment, author, branch, ct: ct);
        if (!first.Ok) return first;
        return await DeriveAsync(branch, localOnly, ct);
    }

    private static ActionResult FrozenReject(AppState s) => new(false, OutcomeKind.SemanticImpossible, null, null,
        new[] { new Diagnostic("FROZEN-CONFLICT",
            $"模型已在事件 {s.FrozenAtEventId} 冻结；新锚点/改动必须在比较分支进行，不能覆盖冻结结论",
            $"candidate:{s.FrozenCandidateId}") }, s, "main");

    private async Task<ActionResult> Append(EventType type, object payload, string fragment, string author,
        string branch, bool derive = false, string? overrideBranchForEvent = null, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions.Opt);
        lock (_gate)
        {
            long seq = _events.Count == 0 ? 1 : _events.Max(e => e.Seq) + 1;
            var state = Projector.Project(_events, branch: overrideBranchForEvent ?? branch);
            var expected = _events.Count == 0 ? null : _events.OrderBy(e => e.Seq).Last().EventId;
            var digest = Sha256(json + "|" + fragment);
            var ev = new StoredEvent
            {
                EventId = CandidateId.Event(type.ToString(), branch, seq),
                Seq = seq, BranchId = overrideBranchForEvent ?? branch,
                ParentEventId = state.LastEventId,
                RuleVersion = state.RuleVersion,
                InputDigest = digest, Type = type, PayloadJson = json,
                SourceFragment = fragment, Author = author,
                OccurredAt = DateTime.UtcNow.ToString("O")
            };
            var res = _store.AppendAsync(ev, expected, ct).GetAwaiter().GetResult();
            if (!res.Ok)
                return new ActionResult(false, OutcomeKind.SemanticImpossible, null, res.ConflictActualLastId,
                    new[] { new Diagnostic("CONCURRENT-UPDATE",
                        "并发写入冲突：期望的父事件已被其他会话推进", expected ?? "∅") }, state, branch);
            _events.Add(res.Stored!);
            return new ActionResult(true, OutcomeKind.Ok, res.Stored!.EventId, null,
                Array.Empty<Diagnostic>(), Projector.Project(_events, branch: overrideBranchForEvent ?? branch), branch);
        }
    }

    /// <summary>Replace the log from an exported package (used after DB wipe on
    /// re-import). Ids and seq are taken verbatim, preserving provenance.</summary>
    public async Task<ActionResult> ImportPackageAsync(ProjectPackage pkg, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var ordered = pkg.Events.OrderBy(e => e.Seq).ToList();
            // Validate continuity and identity uniqueness up-front (format-level ok,
            // semantic-level if provenance broken).
            var ids = new HashSet<string>();
            foreach (var e in ordered)
            {
                if (!ids.Add(e.EventId))
                    return new ActionResult(false, OutcomeKind.SemanticImpossible, null, null,
                        new[] { new Diagnostic("PKG-DUPID", $"导入包含重复事件身份 {e.EventId}", "events") }, null, "main");
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                var written = _store.AppendAsync(ordered[i],
                    i == 0 ? null : ordered[i - 1].EventId, ct).GetAwaiter().GetResult();
                if (!written.Ok)
                    return new ActionResult(false, OutcomeKind.SemanticImpossible, null, written.ConflictActualLastId,
                        new[] { new Diagnostic("PKG-ORDER", "导入顺序断裂或并发冲突", $"event:{ordered[i].EventId}") }, null, "main");
                _events.Add(ordered[i]);
            }
            return new ActionResult(true, OutcomeKind.Ok, ordered[^1].EventId, null,
                Array.Empty<Diagnostic>(), Projector.Project(_events), "main");
        }
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
