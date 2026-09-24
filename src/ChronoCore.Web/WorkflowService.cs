using System.Text.Json;
using ChronoCore.Domain.Engine;
using ChronoCore.Domain.Ingest;
using ChronoCore.Domain.Model;
using ChronoCore.Storage;

namespace ChronoCore.Web;

/// <summary>Server-side application workflow. Controllers and tests both call
/// this service directly: import -> derive -> accept/reject/adjust -> freeze
/// -> compare-new-anchor, plus the counterexample archive. No business rule
/// is delegated to the browser.</summary>
public sealed class WorkflowService
{
    private readonly ChronoStore _store;
    public const string MainBranch = "main";
    private readonly object _gate = new();

    public WorkflowService(ChronoStore store) => _store = store;

    public CorePackage Parse(string sourceId, string rawText, ParseBudget? budget = null)
    {
        var read = PackageReader.Read(sourceId, rawText, budget);
        if (read.BudgetHit)
            throw new WorkflowException(WorkflowOutcome.BudgetExhausted, "BUDGET_EXHAUSTED",
                "解析资源预算耗尽（与格式损坏、语义无解明确区分）", read.Diagnostics);
        if (read.Package is null)
            throw new WorkflowException(WorkflowOutcome.CorruptFormat, "FORMAT_CORRUPT",
                "材料格式损坏或缺少必填声明，导入中止", read.Diagnostics);
        if (read.Diagnostics.Any(d => d.Severity == DiagSeverity.SemanticUnsolvable))
            throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "SEMANTIC_UNSOLVABLE",
                "材料格式可解析但存在语义无解项，必须显式处理后再导入", read.Diagnostics);
        return read.Package;
    }

    public sealed record ImportResult(ChronoEvent Event, string PackageId, CorePackage Package,
        IReadOnlyList<Diagnostic> Diagnostics);

    public ImportResult Import(string sourceId, string rawText, string branchId = MainBranch,
        EventKind kind = EventKind.PackageImported, string note = "")
    {
        lock (_gate)
        {
            var read = PackageReader.Read(sourceId, rawText);
            if (read.BudgetHit)
                throw new WorkflowException(WorkflowOutcome.BudgetExhausted, "BUDGET_EXHAUSTED",
                    "解析资源预算耗尽", read.Diagnostics);
            if (read.Package is null)
                throw new WorkflowException(WorkflowOutcome.CorruptFormat, "FORMAT_CORRUPT",
                    "材料格式损坏", read.Diagnostics);
            var pkg = read.Package;
            var stored = _store.SavePackage(pkg.CaseId, pkg.CaseVersion, sourceId, rawText, pkg);
            var parent = Head(branchId)?.EventId;
            var ev = _store.AppendEvent(kind, branchId, parent, stored.PackageId,
                Ruleset.V1.Version, stored.Digest,
                new ImportPayload(stored.PackageId, pkg.CaseId, pkg.CaseVersion, note));
            return new ImportResult(ev, stored.PackageId, pkg, read.Diagnostics);
        }
    }

    public sealed record DeriveResult(ChronoEvent Event, EngineResult Result, string DerivationId);

    public DeriveResult Derive(string branchId = MainBranch, string? rulesetVersion = null)
    {
        lock (_gate)
        {
            var state = StateAtHead(branchId);
            if (state.Package is null)
                throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "NO_MATERIAL",
                    "尚无不可变材料，无法推导", Array.Empty<Diagnostic>());
            var rules = rulesetVersion is null ? state.Ruleset : Rulesets.Get(rulesetVersion);
            var result = AgeModelEngine.Solve(state.Package, rules);
            var parent = LastDerivationEvent(branchId)?.EventId ?? "ROOT";
            var ev = _store.AppendEvent(EventKind.DerivationRun, branchId, state.Head.EventId,
                state.PackageId, rules.Version, result.InputDigest,
                new RulesetPayload(rules.Version, "derive"));
            var derivationId = "DRV-" + Stable.Short(ev.EventId + result.RulesetFingerprint);
            _store.SaveDerivation(derivationId, ev.EventId, state.PackageId!, result, parent);
            return new DeriveResult(ev, result, derivationId);
        }
    }

    public ChronoEvent Decide(string candidateId, bool accept, string reason, string branchId = MainBranch)
    {
        lock (_gate)
        {
            var state = StateAtHead(branchId);
            if (state.Result is null)
                throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "NO_DERIVATION",
                    "尚未推导，不能接受/否定候选", Array.Empty<Diagnostic>());
            if (!state.Result.Candidates.Any(c => c.Id == candidateId))
                throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "CANDIDATE_UNKNOWN",
                    $"候选 {candidateId} 不属于当前推导结果", Array.Empty<Diagnostic>());
            var kind = accept ? EventKind.CandidateAccepted : EventKind.CandidateRejected;
            return _store.AppendEvent(kind, branchId, state.Head.EventId, state.PackageId,
                state.Ruleset.Version, state.Result.InputDigest,
                new DecisionPayload(candidateId, reason, state.Head.EventId));
        }
    }

    public ChronoEvent AdjustParameters(string rulesetVersion, string note, string branchId = MainBranch)
    {
        lock (_gate)
        {
            var rules = Rulesets.Get(rulesetVersion);
            var state = StateAtHead(branchId);
            return _store.AppendEvent(EventKind.ParameterAdjusted, branchId, state.Head.EventId,
                state.PackageId, rules.Version, state.Result?.InputDigest ?? "",
                new RulesetPayload(rulesetVersion, note));
        }
    }

    public ChronoEvent Freeze(string candidateId, string branchId = MainBranch, int? expectedLock = null)
    {
        lock (_gate)
        {
            var state = StateAtHead(branchId);
            if (state.Result is null || !state.Result.Candidates.Any(c => c.Id == candidateId))
                throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "CANDIDATE_UNKNOWN",
                    "候选不存在，无法冻结", Array.Empty<Diagnostic>());
            return _store.AppendEvent(EventKind.CandidateFrozen, branchId, state.Head.EventId,
                state.PackageId, state.Ruleset.Version, state.Result.InputDigest,
                new DecisionPayload(candidateId, "freeze", state.Head.EventId),
                frozenCandidateId: candidateId, expectedLockVersion: expectedLock);
        }
    }

    /// <summary>Compare a proposed new anchor against a frozen model. Frozen
    /// conclusions are never overwritten; the comparison is read-only and the
    /// impact is returned. Conflicts are a distinct outcome.</summary>
    public sealed record CompareResult(bool Allowed, string? ConflictCode, string? Message,
        ImpactReport? Report, EngineResult? TrialResult);

    public CompareResult CompareNewAnchor(string newAnchorDacLine, string branchId = MainBranch)
    {
        lock (_gate)
        {
            var frozen = _store.GetFreeze(branchId)
                ?? throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "NOT_FROZEN",
                    "分支尚未冻结模型，无法进行冻结后比较", Array.Empty<Diagnostic>());
            var baseline = StateAtHead(branchId);
            if (baseline.Package is null) throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable,
                "NO_MATERIAL", "无材料", Array.Empty<Diagnostic>());

            var trial = AppendAnchorTrial(baseline.Package, newAnchorDacLine, out var newId);
            var trialResult = AgeModelEngine.Solve(trial, baseline.Ruleset);
            var baseResult = baseline.Result ?? AgeModelEngine.Solve(baseline.Package, baseline.Ruleset);

            var guard = FreezeGuard.TryCompareNewAnchor(
                new FreezeGuard.FreezeDecision(frozen.CandidateId, frozen.Digest, frozen.Ruleset),
                trialResult, newId);
            var report = ImpactAnalyzer.Compare(baseline.Package, baseResult, trial,
                newId, "NEW_ANCHOR_PROPOSAL", trialResult, baseline.Ruleset);
            return new CompareResult(guard.Outcome == FreezeGuard.FreezeOutcome.Allowed,
                guard.ConflictCode, guard.Message, report, trialResult);
        }
    }

    private CorePackage AppendAnchorTrial(CorePackage baseline, string anchorLine, out string newAnchorId)
    {
        // Build an in-memory trial source: full baseline raw + one ANCHOR line.
        var trialText = string.Join("\n", baseline.RawLines.Select(r => r.Text)) + "\n" + anchorLine + "\n";
        var read = PackageReader.Read(baseline.CaseId + ":trial", trialText);
        if (read.Package is null)
            throw new WorkflowException(WorkflowOutcome.CorruptFormat, "TRIAL_ANCHOR_BAD",
                "新锚点片段无法解析", read.Diagnostics);
        var added = read.Package.Anchors.First(a => !baseline.Anchors.Any(b => b.Id == a.Id));
        newAnchorId = added.Id;
        return read.Package;
    }

    /// <summary>Create the counterexample archive: preserve the baseline,
    /// change exactly one direct source, solve both, retain adopted AND
    /// rejected outcomes and machine-readable impact events.</summary>
    public CounterfactualRecord Counterfactual(string label, string baselineRaw, string changedRaw,
        string changedMaterial, string changeKind, string branchId = MainBranch)
    {
        lock (_gate)
        {
            var basePkg = Parse("counterfactual:baseline", baselineRaw);
            var changedPkg = Parse("counterfactual:changed", changedRaw);
            var baseStored = _store.SavePackage(basePkg.CaseId, basePkg.CaseVersion,
                "counterfactual:baseline", baselineRaw, basePkg);
            var changedStored = _store.SavePackage(changedPkg.CaseId, changedPkg.CaseVersion,
                "counterfactual:changed", changedRaw, changedPkg);

            var baseResult = AgeModelEngine.Solve(basePkg, Ruleset.V1);
            var changedResult = AgeModelEngine.Solve(changedPkg, Ruleset.V1);
            var report = ImpactAnalyzer.Compare(basePkg, baseResult, changedPkg,
                changedMaterial, changeKind, changedResult, Ruleset.V1);

            var archiveId = "CF-" + Stable.Short(label + baseStored.PackageId + changedStored.PackageId);
            var rec = new CounterfactualRecord(archiveId, branchId, label,
                baseStored.PackageId, changedStored.PackageId, changedMaterial, changeKind,
                report, report.AdoptedOutcomeIds, report.RejectedOutcomeIds);
            _store.SaveCounterfactual(rec);
            return rec;
        }
    }

    // ---- projections --------------------------------------------------------

    public sealed record HeadState(ChronoEvent Head, CorePackage? Package, string? PackageId,
        Ruleset Ruleset, EngineResult? Result);

    public HeadState StateAtHead(string branchId = MainBranch)
    {
        var events = _store.Events(branchId);
        if (events.Count == 0)
            throw new WorkflowException(WorkflowOutcome.SemanticUnsolvable, "EMPTY_BRANCH",
                "分支为空", Array.Empty<Diagnostic>());
        var head = events[^1];
        var packageId = events.LastOrDefault(e => e.MaterialPackageId is not null)?.MaterialPackageId;
        CorePackage? pkg = null;
        if (packageId is not null)
        {
            var raw = _store.RawText(packageId)!;
            pkg = PackageReader.Read(packageId, raw).Package!;
        }
        var ruleset = Rulesets.Get(events.LastOrDefault(e =>
            e.Kind is EventKind.RulesetChosen or EventKind.ParameterAdjusted)?.RulesetVersion
            ?? Ruleset.V1.Version);
        var lastDerive = events.LastOrDefault(e => e.Kind == EventKind.DerivationRun);
        EngineResult? result = null;
        if (pkg is not null) result = AgeModelEngine.Solve(pkg, ruleset);
        return new HeadState(head, pkg, packageId, ruleset, result);
    }

    public ReplayState ReplayTo(string eventId, string branchId = MainBranch)
    {
        var events = _store.Events(branchId);
        var packages = new Dictionary<string, CorePackage>();
        foreach (var p in _store.ListPackages())
        {
            var parsed = PackageReader.Read(p.PackageId, p.Raw).Package;
            if (parsed is not null) packages[p.PackageId] = parsed;
        }
        return Replay.ToEvent(events, packages, eventId);
    }

    public IReadOnlyList<ChronoEvent> Timeline(string branchId = MainBranch) => _store.Events(branchId);
    public IReadOnlyList<CounterfactualRecord> Counterfactuals() => _store.ListCounterfactuals();

    private ChronoEvent? Head(string branchId)
    {
        var evs = _store.Events(branchId);
        return evs.Count == 0 ? null : evs[^1];
    }

    private ChronoEvent? LastDerivationEvent(string branchId) =>
        _store.Events(branchId).LastOrDefault(e => e.Kind == EventKind.DerivationRun);

    public ExportBundle Export() => _store.Export();
    public void ImportBundle(ExportBundle bundle) => _store.Import(bundle);
    public bool QuickCheck() => _store.QuickCheck();
}

public enum WorkflowOutcome { Ok, BudgetExhausted, CorruptFormat, SemanticUnsolvable }

public sealed class WorkflowException : Exception
{
    public WorkflowOutcome Outcome { get; }
    public string Code { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public WorkflowException(WorkflowOutcome outcome, string code, string message,
        IReadOnlyList<Diagnostic> diagnostics) : base(message)
    {
        Outcome = outcome; Code = code; Diagnostics = diagnostics;
    }
}
