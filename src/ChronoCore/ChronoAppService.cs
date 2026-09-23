using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoCore;

/// <summary>应用编排：所有“导入/推导/复核”动作都产生结构化事件并可重放。</summary>
public sealed class ChronoAppService
{
    public const string ProjectId = "proj-core7b";
    public const string MainBranch = "main";
    private static readonly JsonSerializerOptions Jo =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IEventSink _sink;
    private int _eventCounter;
    public DerivationEngine Engine { get; } = new();
    public DateTimeOffset Clock { get; set; } = DateTimeOffset.UtcNow;

    public ChronoAppService(IEventSink sink) { _sink = sink; }

    private string NewEventId(string prefix)
    {
        var n = Interlocked.Increment(ref _eventCounter);
        return prefix + "-" + Clock.ToUnixTimeSeconds() + "-" + n.ToString("000");
    }

    public ChronoEvent Emit(string branch, string type, object payload,
        string inputDigest = "", string? paramVersion = null,
        string? derivedModelId = null, bool adopted = true,
        IEnumerable<string>? parents = null, string actor = "sed-team",
        IReadOnlyList<DerivedModel>? snapshots = null,
        string? freezeModelId = null, string? freezeHolderActor = null)
    {
        var ev = new ChronoEvent(0, NewEventId(type.Replace('.', '-')), branch, type,
            Clock.AddSeconds(_eventCounter).ToString("O"), actor,
            JsonNode.Parse(JsonSerializer.Serialize(payload, Jo))!,
            (parents ?? Array.Empty<string>()).ToList(), inputDigest,
            paramVersion, derivedModelId, adopted);
        return _sink.Append(ev, snapshots, freezeModelId, freezeHolderActor, actor);
    }

    // ---- 基线播种 ------------------------------------------------------------
    public void SeedFromCase(CaseBundle b)
    {
        _sink.EnsureProject(ProjectId, b.Title);
        Emit(MainBranch, EventType.ProjectCreated,
            new ProjectCreatedPayload(b.Title, MainBranch, b.Narrative), adopted: true);

        foreach (var d in b.Baseline.Datums)
            Material(MainBranch, "datum", d.Id, d);
        foreach (var g in b.Baseline.Gaps)
            Material(MainBranch, "gap", g.Id, g);
        foreach (var iv in b.Baseline.Intervals)
            Material(MainBranch, "interval", iv.Id, iv);
        foreach (var r in b.Baseline.Reservoirs)
            Material(MainBranch, "reservoir", r.Id, r);
        foreach (var e in b.Baseline.EventLayers)
            Material(MainBranch, "event", e.Id, e);
        foreach (var a in b.Baseline.Anchors)
            Material(MainBranch, "anchor", a.Id, a,
                unconvertible: Quantity.Depth(a.DepthRaw, a.DatumId).IsConvertible ? null
                    : Quantity.Depth(a.DepthRaw, a.DatumId).Diagnostic);
        foreach (var ex in b.Baseline.Exclusions)
            Emit(MainBranch, EventType.MaterialExcluded, new MaterialExcludedPayload(ex),
                parents: new[] { "material:" + ex.MaterialId });
        foreach (var p in b.Baseline.Parameters)
            Emit(MainBranch, EventType.ParameterRevised,
                new ParameterRevisedPayload(p, p.Version == "v1" ? "" : "v" + (p.Ord - 1)),
                paramVersion: p.Version);
    }

    private void Material(string branch, string kind, string id, object material,
        string? baselineDigest = null, string? changedSource = null,
        string? unconvertible = null)
    {
        var raw = JsonSerializer.Serialize(material, Jo);
        Emit(branch, EventType.MaterialImported,
            new MaterialImportedPayload(kind, id, baselineDigest, changedSource, raw, unconvertible),
            parents: new[] { kind + ":" + id });
    }

    // ---- 派生 ----------------------------------------------------------------
    public DeriveOutcome DeriveAndRecord(string branch, ProjectState state, ParameterSet p,
        IReadOnlyCollection<string>? localOnly = null, string actor = "sed-team",
        string? forkDigest = null, string? changedSource = null)
    {
        var outcome = Engine.Derive(state, p, localOnly);
        if (outcome.Kind == OutcomeKind.Success)
        {
            var valid = outcome.Candidates.Where(m => m.SemanticallyValid).ToList();
            var payload = new ModelsDerivedPayload(outcome.InputDigest, p.Version,
                outcome.Candidates.Select(m => m.Id).ToList(),
                valid.Select(m => m.Id).ToList(),
                nameof(OutcomeKind.Success),
                outcome.Diagnostics.Select(d => d.Code).Distinct().ToList(),
                localOnly is null ? null : string.Join(",", localOnly),
                outcome.BudgetRequested, outcome.BudgetUsed);
            var type = localOnly is null ? EventType.ModelsDerived : EventType.LocalRecompute;
            var parents = outcome.ParentDependencies
                .Append(localOnly is null ? "derive:full" : "derive:local")
                .Concat(state.Derived.Keys.TakeLast(4)).ToList();
            Emit(branch, type, payload, outcome.InputDigest, p.Version,
                derivedModelId: valid.FirstOrDefault()?.Id, parents: parents, actor: actor,
                snapshots: outcome.Candidates);
        }
        else
        {
            var code = outcome.Kind == OutcomeKind.BudgetExhausted
                ? EventType.BudgetExhausted
                : outcome.Kind == OutcomeKind.FormatCorrupted
                    ? EventType.FormatCorrupted
                    : EventType.SemanticImpossible;
            Emit(branch, code, new ModelsDerivedPayload(outcome.InputDigest, p.Version,
                Array.Empty<string>(), Array.Empty<string>(), outcome.Kind.ToString(),
                outcome.Diagnostics.Select(d => d.Code).ToList(),
                localOnly is null ? null : string.Join(",", localOnly),
                outcome.BudgetRequested, outcome.BudgetUsed),
                outcome.InputDigest, p.Version, adopted: false, actor: actor,
                parents: outcome.ParentDependencies);
        }
        return outcome;
    }

    public void RecordDecision(string branch, Decision d, bool conflicts, string? prior)
        => Emit(branch, EventType.DecisionMade, new DecisionPayload(d, conflicts, prior),
            paramVersion: d.ReplacementParameterVersion, parents: new[] { "model:" + d.CandidateModelId });

    public void ReviseMaterial(string branch, string kind, string materialId, object newMaterial,
        string previousEventId, string scope = "full", string actor = "sed-team")
    {
        var raw = JsonSerializer.Serialize(newMaterial, Jo);
        Emit(branch, EventType.MaterialRevised,
            new MaterialRevisedPayload(kind, materialId, previousEventId, raw, null, null, scope),
            parents: new[] { kind + ":" + materialId, "event:" + previousEventId }, actor: actor);
    }

    // ---- 冻结与影响 ----------------------------------------------------------
    public bool TryFreeze(string branch, string modelId, string inputDigest, string paramVersion,
        string actor, out ChronoEvent? conflictEvent, out string? holder)
    {
        conflictEvent = null; holder = null;
        try
        {
            Emit(branch, EventType.ModelFrozen,
                new ModelFrozenPayload(modelId, inputDigest, paramVersion),
                inputDigest, paramVersion, modelId, actor: actor,
                freezeModelId: modelId, freezeHolderActor: actor);
            return true;
        }
        catch (ConcurrentFreezeConflict ex)
        {
            holder = ex.HolderActor;
            conflictEvent = Emit(branch, EventType.FreezeConflict,
                new FreezeConflictPayload(modelId, ex.HolderActor, actor, ex.HeldSinceEventId,
                    "并发冻结冲突：冻结不可覆盖，后到决定记为未采纳冲突事件"),
                inputDigest, paramVersion, modelId, adopted: false, actor: actor);
            return false;
        }
    }

    public void RecordImpact(string branch, ImpactReport report, bool adopt, string actor = "reviewer")
        => Emit(branch, EventType.ImpactCompared,
            new ImpactPayload(report.FrozenModelId, report.NewAnchorId, report.MonotonicPreserved,
                report.MaxAgeShiftCalA, report.AffectedIntervalIds,
                report.Diagnostics.Select(d => d.Code).Distinct().ToList(), adopt),
            report.InputDigest, adopted: adopt, actor: actor,
            parents: new[] { "model:" + report.FrozenModelId, "anchor:" + report.NewAnchorId });

    public void RecordJudgment(string branch, JudgmentPayload j, string actor = "replay-runner")
        => Emit(branch, EventType.ReplayJudgment, j,
            parents: j.EvidenceEventIds.Concat(j.InputFragmentIds).ToList(), adopted: j.Holds, actor: actor);

    public void ForkBranch(string newBranch, string parentBranch, long fromSeq,
        string? baselineDigest, string? changedSource, string purpose)
    {
        _sink.Fork(newBranch, parentBranch, fromSeq, ProjectId, baselineDigest, changedSource, purpose);
        Emit(newBranch, EventType.BranchForked,
            new BranchForkedPayload(parentBranch, newBranch, fromSeq, baselineDigest, changedSource, purpose),
            baselineDigest ?? "", parents: new[] { "branch:" + parentBranch });
    }
}
