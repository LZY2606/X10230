using ChronoCore.Domain;
using ChronoCore.Engine;
using System.Text.Json;

namespace ChronoCore.Storage;

public enum WorkflowOutcome { Committed = 0, BranchFrozen = 1, FreezeRaced = 2, Rejected = 3 }

public sealed record WorkflowResponse(WorkflowOutcome Outcome, LedgerEvent? Event, DerivationResult? Result, string Message = "");

/// <summary>
/// 项目工作流：不可变材料追加、可修改决定登记、引擎推导、冻结与并发控制。
/// 每次接受/否定/参数调整都产生新事件；回旧事件读回的是当时的依赖版本与结果。
/// </summary>
public sealed class ProjectWorkflowService
{
    private readonly ProjectRepository _repo;
    private readonly AgeModelEngine _engine = new();
    private readonly object _gate = new();

    public ProjectWorkflowService(ProjectRepository repo) => _repo = repo;

    public ProjectRecord Require(string projectId) =>
        _repo.LoadProject(projectId) ?? throw new InvalidOperationException($"项目 {projectId} 不存在");

    public DerivationResult? ResultAt(string projectId, string eventId)
    {
        var p = Require(projectId);
        var ev = p.Events.FirstOrDefault(e => e.Id == eventId);
        if (ev?.ResultRef is null) return null;
        var json = _repo.LoadResultJson(ev.ResultRef);
        return json is null ? null : JsonSerializer.Deserialize<DerivationResult>(json, Web.JsonOptions.Web)!;
    }

    public DerivationResult? LatestResult(string projectId, string branchId = "main")
    {
        var p = Require(projectId);
        var ev = p.Events.Where(e => e.BranchId == branchId && e.ResultRef is not null)
            .OrderByDescending(e => e.Seq).FirstOrDefault();
        return ev is null ? null : ResultAt(projectId, ev.Id);
    }

    /// <summary>导入原始材料（只追加，不覆盖）。</summary>
    public LedgerEvent ImportMaterials(string projectId, IReadOnlyList<Material> incoming, string actor = "importer")
    {
        lock (_gate)
        {
            var p = Require(projectId);
            foreach (var m in incoming) p.Materials.Add(m);
            var ps = CurrentParameters(p);
            var ev = NewEvent(p, "main", "materials.imported", ps,
                new { count = incoming.Count, ids = incoming.Select(m => m.Id).Order() }, p.Events.LastOrDefault(e => e.BranchId == "main")?.Id);
            Persist(p, ev, null);
            return ev;
        }
    }

    /// <summary>核心工作流：登记决定/参数切换/局部改动后推导，并将输入摘要、参数版本、父级依赖写入事件。</summary>
    public WorkflowResponse DeriveAndCommit(
        string projectId, string branchId, string eventType, object payload,
        string parameterSetId, string actor = "reviewer", string? scopeMaterialId = null)
    {
        lock (_gate)
        {
            var p = Require(projectId);
            var branch = p.Branches.FirstOrDefault(b => b.Id == branchId);
            if (branch is null) return new(WorkflowOutcome.Rejected, null, null, $"分支 {branchId} 不存在");

            var ps = p.ParameterSets.FirstOrDefault(x => x.Id == parameterSetId) ?? CurrentParameters(p);
            var parent = p.Events.Where(e => e.BranchId == branchId).OrderByDescending(e => e.Seq).FirstOrDefault();

            // 冻结后：主分支只读；新锚点影响必须走 trial 分支
            if (branch.Frozen)
                return new(WorkflowOutcome.BranchFrozen, null, null,
                    $"分支 {branchId} 已冻结于 {branch.FrozenAtEventId}（digest {branch.FreezeDigest?[..12]}）；拒绝在冻结视图上追加修改");

            var result = _engine.Derive(p.Materials, p.Decisions, ps, branchId,
                parentResultId: parent?.ResultRef, scopeMaterialId: scopeMaterialId);
            if (scopeMaterialId is not null && result.Outcome == EngineOutcome.Ok)
            {
                var rateSegs = result.Candidates.SelectMany(c => c.Rates)
                    .Where(r => ScopeOverlap(result.Anchors, scopeMaterialId, r)).Select(r => r.SegmentId);
                result.RecomputedSegmentIds = result.Anchors
                    .Where(a => a.SourceMaterialId == scopeMaterialId).Select(a => a.SegmentId)
                    .Concat(rateSegs).Distinct().Order().ToList();
            }

            var ev = NewEvent(p, branchId, eventType, ps, payload, parent?.Id, result);
            Persist(p, ev, result);
            return new(WorkflowOutcome.Committed, ev, result);
        }
    }

    private static bool ScopeOverlap(List<Engine.EffectiveAnchor> anchors, string scopeId, Engine.RateInterval r)
    {
        var scopeDepth = anchors.FirstOrDefault(a => a.SourceMaterialId == scopeId)?.DepthMbsf;
        return scopeDepth is double d && d >= r.TopDepth - 1e-9 && d <= r.BaseDepth + 1e-9;
    }

    /// <summary>登记一条可修改决定（接受/否定/参数调整均生成事件）。</summary>
    public WorkflowResponse RecordDecision(string projectId, string branchId, Decision decision, object payload, string actor = "reviewer")
    {
        lock (_gate)
        {
            var p = Require(projectId);
            var branch = p.Branches.FirstOrDefault(b => b.Id == branchId);
            if (branch?.Frozen == true)
                return new(WorkflowOutcome.BranchFrozen, null, null, "分支已冻结，决定必须登记在新分支");

            var conflicting = p.Decisions.FirstOrDefault(d =>
                d.TargetMaterialId == decision.TargetMaterialId && d.Status == DecisionStatus.Accepted &&
                ((d.Kind == DecisionKind.Exclude && decision.Kind == DecisionKind.Reinstate) ||
                 (d.Kind == DecisionKind.Reinstate && decision.Kind == DecisionKind.Exclude)));
            if (conflicting is not null && decision.Status == DecisionStatus.Accepted)
            {
                decision.Status = DecisionStatus.Conflict;
                decision.ConflictsWithDecisionId = conflicting.Id;
            }
            decision.CreatedAt = DateTime.UtcNow;
            decision.Id = string.IsNullOrEmpty(decision.Id) ? "dec_" + Guid.NewGuid().ToString("N")[..10] : decision.Id;
            p.Decisions.Add(decision);

            var ps = CurrentParameters(p);
            var parent = p.Events.Where(e => e.BranchId == branchId).OrderByDescending(e => e.Seq).FirstOrDefault();
            var result = _engine.Derive(p.Materials, p.Decisions, ps, branchId, parent?.ResultRef);
            var ev = NewEvent(p, branchId, decision.Kind == DecisionKind.ParameterRevision ? "params.revised" : "decision.recorded",
                ps, payload, parent?.Id, result);
            decision.EventId = ev.Id;
            Persist(p, ev, result, upsertDecision: decision);
            return new(WorkflowOutcome.Committed, ev, result,
                decision.Status == DecisionStatus.Conflict ? $"与 {conflicting?.Id} 冲突，已留档为 Conflict" : "决定已登记");
        }
    }

    /// <summary>冻结模型；并发冻结第二笔必须检测到摘要已存在并返回竞态失败。</summary>
    public WorkflowResponse Freeze(string projectId, string branchId, string actor = "reviewer")
    {
        lock (_gate)
        {
            var p = Require(projectId);
            var branch = p.Branches.FirstOrDefault(b => b.Id == branchId)
                ?? throw new InvalidOperationException("分支不存在");
            if (branch.Frozen)
                return new(WorkflowOutcome.FreezeRaced, null, null,
                    $"并发冻结冲突：{branchId} 已被另一会话冻结于 {branch.FrozenAtEventId}");

            var ps = CurrentParameters(p);
            var parent = p.Events.Where(e => e.BranchId == branchId).OrderByDescending(e => e.Seq).FirstOrDefault();
            var result = _engine.Derive(p.Materials, p.Decisions, ps, branchId, parent?.ResultRef);
            var digest = result.InputDigest;
            var ev = NewEvent(p, branchId, "model.frozen", ps, new { digest, actor }, parent?.Id, result);
            branch.FrozenAtEventId = ev.Id;
            branch.FreezeDigest = digest;
            Persist(p, ev, result, freezeBranch: branch);
            return new(WorkflowOutcome.Committed, ev, result, "模型已冻结：输入摘要固定，历史结果不可被最新计算覆盖");
        }
    }

    /// <summary>从冻结点拉试验分支，在其上比较新锚点影响。</summary>
    public WorkflowResponse CreateTrialFromFreeze(string projectId, string frozenBranchId, string trialBranchId, Material newAnchor, object payload)
    {
        lock (_gate)
        {
            var p = Require(projectId);
            var frozen = p.Branches.FirstOrDefault(b => b.Id == frozenBranchId)
                ?? throw new InvalidOperationException("冻结分支不存在");
            if (!frozen.Frozen) return new(WorkflowOutcome.Rejected, null, null, "只能从已冻结分支创建试验");
            if (p.Branches.Any(b => b.Id == trialBranchId))
                return new(WorkflowOutcome.FreezeRaced, null, null, "同名试验分支已存在（并发冲突）");

            var anchor = newAnchor.ShallowCopy();
            anchor.ImportedAt = DateTime.UtcNow;
            p.Materials.Add(anchor);
            p.Branches.Add(new Branch { Id = trialBranchId, Name = "新锚点影响试验（自冻结点 " + frozen.FrozenAtEventId?[..10] + "）", CreatedAt = DateTime.UtcNow });

            var ps = CurrentParameters(p);
            var result = _engine.Derive(p.Materials, p.Decisions, ps, trialBranchId, scopeMaterialId: anchor.Id);
            var ev = NewEvent(p, trialBranchId, "anchor.trial", ps, payload, frozen.FrozenAtEventId, result);
            Persist(p, ev, result);
            return new(WorkflowOutcome.Committed, ev, result, "试验分支已建：与冻结摘要比较新锚点的局部影响");
        }
    }

    private static ParameterSet CurrentParameters(ProjectRecord p) =>
        p.ParameterSets.OrderByDescending(x => x.Version).First();

    private LedgerEvent NewEvent(ProjectRecord p, string branchId, string type, ParameterSet ps,
        object payload, string? parentEventId, DerivationResult? result = null)
    {
        var seq = _repo.NextEventSeq(p.Id, branchId);
        var ev = new LedgerEvent
        {
            Seq = seq,
            Id = $"evt-{branchId}-{seq:D3}",
            BranchId = branchId,
            ParentEventId = parentEventId,
            Type = type,
            ParameterSetId = ps.Id,
            InputDigest = AgeModelEngine.InputDigest(p.Materials, p.Decisions, ps),
            PayloadJson = JsonSerializer.Serialize(payload, Web.JsonOptions.Web),
            ResultRef = result?.ResultId,
            CreatedAt = DateTime.UtcNow
        };
        p.Events.Add(ev);
        return ev;
    }

    private void Persist(ProjectRecord p, LedgerEvent ev, DerivationResult? result,
        Decision? upsertDecision = null, Branch? freezeBranch = null)
    {
        if (result is not null && !p.Results.Any(r => r.ResultId == result.ResultId))
            p.Results.Add(result);
        if (upsertDecision is not null && !p.Decisions.Any(d => d.Id == upsertDecision.Id))
            p.Decisions.Add(upsertDecision);
        if (freezeBranch is not null)
        {
            var existing = p.Branches.FirstOrDefault(b => b.Id == freezeBranch.Id);
            if (existing is not null)
            {
                existing.FrozenAtEventId = freezeBranch.FrozenAtEventId;
                existing.FreezeDigest = freezeBranch.FreezeDigest;
            }
            else p.Branches.Add(freezeBranch);
        }
        _repo.SaveProject(p);
    }
}
