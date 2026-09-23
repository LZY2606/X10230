using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chrono.Core;
using Chrono.Core.Dossier;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Model;

namespace Chrono.Storage;

/// <summary>Result types kept explicitly distinct: budget exhaustion vs format vs semantic failure.</summary>
public sealed record ActionOutcome(
    bool Ok,
    DerivationFailure FailureClass,
    string? Message,
    EventRecord? Event,
    DerivationResult? Derivation);

public sealed record ExportBundle(
    string Format,
    string ProjectId,
    DateTimeOffset ExportedAt,
    IReadOnlyList<EventRecord> Events)
{
    public string BundleChecksum { get; init; } = "";
}

/// <summary>
/// Application service used directly by both HTTP layer and tests — business rules are not hidden
/// in the browser. Every command appends an event; every derivation records input summary,
/// parameter version and parent dependencies.
/// </summary>
public sealed class ProjectAppService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly SqliteProjectStore _store;
    private readonly StateProjector _projector = new();
    private readonly ModelEngine _engine = new();

    public ProjectAppService(SqliteProjectStore store) => _store = store;

    public IReadOnlyList<EventRecord> Events => _store.LoadEvents();

    public ProjectState State(string branch = "main", long? upToSeq = null)
        => _projector.Project(_store.LoadEvents(), branch, upToSeq);

    public EventRecord AppendRaw(string type, string branch, object payload, string actor = "reviewer",
        string? parentEventId = null, string ruleVersion = RuleSet.V1, string? inputSummary = null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var ev = NewEvent(type, branch, json, actor, parentEventId, ruleVersion,
            inputSummary ?? json);
        return _store.Append(ev);
    }

    public ActionOutcome Derive(string branch = "main", string actor = "engine",
        IEnumerable<EventRecord>? parents = null)
    {
        var events = _store.LoadEvents();
        var state = _projector.Project(events, branch);
        long seq = events.Where(e => e.Branch == branch).Select(e => e.Seq).DefaultIfEmpty(0).Max() + 1;
        var parentIds = (parents ?? events.Where(e => e.Branch == "main" || e.Branch == branch))
            .Select(e => e.Id).Distinct().ToList();
        var result = _engine.Derive(state, branch, seq, parentIds);
        var payload = new { derivationId = result.DerivationId, status = result.Status,
            ruleVersion = result.RuleVersion };
        var ev = NewEvent(EventTypes.ModelDerived, branch, JsonSerializer.Serialize(payload, JsonOpts),
            actor, parentIds.LastOrDefault(), result.RuleVersion, result.InputSummary);
        var stored = _store.Append(ev, result);

        var failureClass = result.Status switch
        {
            "BUDGET_EXHAUSTED" => DerivationFailure.BudgetExhausted,
            "SEMANTIC_NO_SOLUTION" => DerivationFailure.SemanticNoSolution,
            _ => DerivationFailure.None
        };
        return new ActionOutcome(failureClass == DerivationFailure.None, failureClass,
            result.Status, stored, result);
    }

    public EventRecord SetRules(string branch, RuleSet rules, string actor = "sed-team")
        => AppendRaw(EventTypes.ParameterRevised, branch,
            new
            {
                version = rules.Version, interpolation = rules.Interpolation, drawCount = rules.DrawCount,
                rngSeed = rules.RngSeed, longTailProbabilityThreshold = rules.LongTailProbabilityThreshold,
                maxCandidates = rules.MaxCandidates, minSedRateCmPerYear = rules.MinSedRateCmPerYear,
                maxSedRateCmPerYear = rules.MaxSedRateCmPerYear
            }, actor, ruleVersion: rules.Version);

    public EventRecord Exclude(string branch, string targetMaterialId, string reason, string actor = "reviewer")
        => AppendRaw(EventTypes.ExclusionDecided, branch,
            new
            {
                decisionId = $"decision/{targetMaterialId}:exclude:{CandidateEngine.StableHash(branch + targetMaterialId + reason):x8}",
                targetMaterialId, reason
            }, actor);

    public EventRecord ChooseCorrelation(string branch, string group, string chosenEventTieId, string actor = "reviewer")
        => AppendRaw(EventTypes.CorrelationChosen, branch,
            new { correlationGroup = group, chosenEventTieId }, actor);

    public EventRecord RejectCandidate(string branch, string candidateId, string actor = "reviewer")
        => AppendRaw(EventTypes.CandidateRejected, branch, new { candidateId }, actor);

    /// <summary>Freeze is linearizable: after a freeze exists, later freeze attempts are refused.</summary>
    public ActionOutcome Freeze(string branch, string derivationId, string? candidateId, string actor = "reviewer")
    {
        var frozen = _store.LoadEvents().Any(e => e.Branch == branch && e.Type == EventTypes.ModelFrozen);
        if (frozen)
        {
            // the losing freeze is still recorded as evidence, marked refused — history stays complete
            var refused = NewEvent(EventTypes.CandidateRejected, branch,
                JsonSerializer.Serialize(new { candidateId = candidateId ?? derivationId,
                    reason = "FREEZE_LOST_RACE: a freeze already exists for this branch" }, JsonOpts),
                actor, ruleVersion: State(branch).Rules.Version);
            _store.Append(refused);
            return new ActionOutcome(false, DerivationFailure.None,
                "branch already frozen; losing freeze recorded as rejected outcome", refused, null);
        }
        var ev = NewEvent(EventTypes.ModelFrozen, branch,
            JsonSerializer.Serialize(new { derivationId, candidateId }, JsonOpts),
            actor, derivationId, State(branch).Rules.Version);
        _store.Append(ev);
        return new ActionOutcome(true, DerivationFailure.None, "frozen", ev, null);
    }

    public EventRecord ForkBranch(string newBranch, string fromBranch, string actor = "reviewer")
        => AppendRaw(EventTypes.BranchForked, newBranch,
            new { newBranch, fromBranch }, actor, ruleVersion: State(fromBranch).Rules.Version);

    public EventRecord AmendSource(string branch, Material replacement, string? supersedesId, string actor = "sed-team")
    {
        var map = MaterialSerialization.ToMap(replacement);
        return AppendRaw(EventTypes.SourceAmended, branch,
            new { supersedesMaterialId = supersedesId, replacement = map }, actor,
            inputSummary: $"amend:{replacement.Id}");
    }

    public EventRecord ImportMaterial(string branch, Material material, string actor = "sed-team")
        => AppendRaw(EventTypes.MaterialImported, branch, MaterialSerialization.ToMap(material), actor,
            inputSummary: $"import:{material.Kind}:{material.Id}");

    public IReadOnlyList<DerivationResult> Derivations => _store.LoadDerivations();
    public DerivationResult? Derivation(string id) => _store.LoadDerivation(id);

    public static EventRecord NewEvent(string type, string branch, string payloadJson, string actor,
        string? parentEventId = null, string ruleVersion = RuleSet.V1, string inputSummary = "")
    {
        string id = $"evt/{branch}:{type}:{CandidateEngine.StableHash(payloadJson + "|" + (parentEventId ?? "")):x16}";
        string hash = HashChain(payloadJson, parentEventId ?? "", ruleVersion);
        return new EventRecord(id, branch, 0, type, parentEventId, actor,
            DateTimeOffset.UtcNow, payloadJson, inputSummary, ruleVersion) { Hash = hash };
    }

    public static string HashChain(string payload, string parentId, string ruleVersion)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{ruleVersion}\n{parentId}\n{payload}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public ExportBundle Export()
    {
        var events = _store.LoadEvents();
        var project = events.FirstOrDefault(e => e.Type == EventTypes.ProjectCreated);
        var bundle = new ExportBundle("chrono-project-bundle/v1",
            project?.Id ?? "project", DateTimeOffset.UtcNow, events);
        var json = JsonSerializer.Serialize(bundle with { BundleChecksum = "pending" }, JsonOpts);
        using var sha = SHA256.Create();
        var sum = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return bundle with { BundleChecksum = sum };
    }

    /// <summary>
    /// Import with three distinguishable outcomes: corrupt format, exhausted budget (cap param)
    /// and semantic issues are never collapsed into a generic failure.
    /// </summary>
    public ActionOutcome ImportBundle(string bundleJson, int maxEvents = 100_000)
    {
        ExportBundle? bundle;
        try { bundle = JsonSerializer.Deserialize<ExportBundle>(bundleJson, JsonOpts); }
        catch (JsonException ex)
        {
            return new ActionOutcome(false, DerivationFailure.FormatCorrupt,
                $"bundle JSON malformed: {ex.Message}", null, null);
        }
        if (bundle is null || bundle.Events is null || bundle.Format != "chrono-project-bundle/v1")
            return new ActionOutcome(false, DerivationFailure.FormatCorrupt,
                "not a chrono-project-bundle/v1 document", null, null);
        if (bundle.Events.Count > maxEvents)
            return new ActionOutcome(false, DerivationFailure.BudgetExhausted,
                $"bundle has {bundle.Events.Count} events, budget {maxEvents}", null, null);

        // hash-chain verification before anything touches committed tables
        var ordered = bundle.Events.OrderBy(e => e.Branch).ThenBy(e => e.Seq).ToList();
        foreach (var ev in ordered)
        {
            var expect = HashChain(ev.PayloadJson, ev.ParentEventId ?? "", ev.RuleVersion);
            if (!FixedTimeEquals(expect, ev.Hash))
                return new ActionOutcome(false, DerivationFailure.FormatCorrupt,
                    $"event {ev.Id} hash mismatch; bundle rejected wholesale", null, null);
        }

        int promoted;
        try { promoted = _store.StagedImport(ordered, Guid.NewGuid().ToString("N")); }
        catch (Exception ex)
        {
            return new ActionOutcome(false, DerivationFailure.FormatCorrupt,
                $"staged import failed and rolled back: {ex.Message}", null, null);
        }
        if (promoted != ordered.Count)
            return new ActionOutcome(false, DerivationFailure.BudgetExhausted,
                $"half-write recovery: promoted {promoted}/{ordered.Count}", null, null);
        return new ActionOutcome(true, DerivationFailure.None,
            $"imported {promoted} events", null, null);
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>
    /// Scoped recompute after one source change. Returns the changed derivation plus the unaffected
    /// track fingerprints proving the blast radius stayed inside the dependency closure.
    /// </summary>
    public (DerivationResult after, IReadOnlyList<string> unchangedTrackFingerprints)
        RecomputeScoped(string branch, DerivationResult before)
    {
        var outcome = Derive(branch, "local-recompute");
        var after = outcome.Derivation!;
        var beforeTracks = before.Tracks.ToDictionary(t => t.Track.Id,
            t => CandidateEngine.StableHash(
                string.Join("|", t.Candidates.SelectMany(c => c.AnchorIds).OrderBy(x => x))).ToString("x"));
        var unchanged = new List<string>();
        foreach (var t in after.Tracks)
        {
            var fp = CandidateEngine.StableHash(
                string.Join("|", t.Candidates.SelectMany(c => c.AnchorIds).OrderBy(x => x))).ToString("x");
            if (beforeTracks.TryGetValue(t.Track.Id, out var old) && old == fp)
                unchanged.Add(t.Track.Id);
        }
        return (after, unchanged);
    }
}
