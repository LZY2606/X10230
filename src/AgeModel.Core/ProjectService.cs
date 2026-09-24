using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace AgeModel.Core;

/// <summary>
/// 应用服务：编排 事件追加（哈希链）→ 引擎推导 → 状态投影。
/// 所有业务规则在服务端执行，浏览器不持有规则真相。
/// </summary>
public sealed partial class ProjectService
{
    private readonly IEventStore _store;
    private readonly Func<EngineParameters, AgeModelEngine> _engineFactory;
    private readonly TimeProvider _clock;
    private static readonly JsonSerializerOptions JsonOpts = new()
    { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public ProjectService(IEventStore store, TimeProvider? clock = null,
        Func<EngineParameters, AgeModelEngine>? engineFactory = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _engineFactory = engineFactory ?? (p => new AgeModelEngine(p, _clock));
    }

    // ---------- 命令 ----------

    public StoredEvent ImportMaterial(string projectId, CoreMaterial material, string actor = "sed-group")
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            projectId,
            material,
            inputSummary = $"core={material.CoreId}; anchors={material.Anchors.Count}; segments={material.Segments.Count}; events={material.Events.Count}; exclusions={material.Exclusions.Count}",
        });
        return Append(projectId, "main", null, EventTypes.MaterialImported, payload, actor);
    }

    public StoredEvent AmendSource(string projectId, string branch, long expectedHeadSeq,
        CoreMaterial newMaterial, IReadOnlyCollection<string> changedSourceIds, string reason, string actor = "sed-group")
    {
        EnsureNotFrozen(projectId, branch);
        var changed = changedSourceIds.OrderBy(x => x).ToList();
        var parentId = LatestEventId(projectId, branch);
        // 局部重算：先推导出受影响对象，随事件一起记录
        var previous = Replay(projectId, branch, expectedHeadSeq);
        var engine = _engineFactory(previous.Parameters);
        var derivation = engine.Derive(newMaterial, parentId, changed);
        var payload = JsonSerializer.SerializeToElement(new
        {
            projectId, changedSourceIds = changed, reason,
            material = newMaterial,
            affectedObjectIds = derivation.AffectedObjectIds,
            localRecompute = true,
        });
        var amendEv = Append(projectId, branch, expectedHeadSeq, EventTypes.SourceAmended, payload, actor,
            $"amend:{string.Join(",", changed)}:{reason}");
        var derivPayload = JsonSerializer.SerializeToElement(new { projectId, derivation });
        Append(projectId, branch, amendEv.Seq, EventTypes.DerivationRecorded, derivPayload, actor,
            derivation.InputFingerprint, derivation.ParameterVersion);
        return amendEv;
    }

    public StoredEvent ReviseParameters(string projectId, string branch, long expectedHeadSeq,
        EngineParameters parameters, string reason, string actor = "sed-group")
    {
        EnsureNotFrozen(projectId, branch);
        var state = Replay(projectId, branch, expectedHeadSeq);
        if (state.Material is null)
            throw new InvalidOperationException("无材料，无法修订参数（输入不完整不得伪装成功）");
        var parentId = LatestEventId(projectId, branch);
        var derivation = _engineFactory(parameters).Derive(state.Material, parentId, new[] { "parameters" });
        var payload = JsonSerializer.SerializeToElement(new { projectId, parameters, reason });
        var rev = Append(projectId, branch, expectedHeadSeq, EventTypes.ParameterRevised, payload, actor,
            reason, parameters.Version);
        var derivPayload = JsonSerializer.SerializeToElement(new { projectId, derivation });
        Append(projectId, branch, rev.Seq, EventTypes.DerivationRecorded, derivPayload, actor,
            derivation.InputFingerprint, parameters.Version);
        return rev;
    }

    public StoredEvent RecordDecision(string projectId, string branch, long expectedHeadSeq,
        string decisionKind, string targetId, bool accepted, string note,
        IReadOnlyCollection<string>? conflictingDecisionIds = null, string actor = "reviewer")
    {
        EnsureNotFrozen(projectId, branch);
        var state = Replay(projectId, branch, expectedHeadSeq);
        var conflicts = conflictingDecisionIds?.OrderBy(x => x).ToList() ?? new List<string>();
        var unresolvedConflict = conflicts.Any(id =>
            state.Decisions.Any(d => d.TryGetProperty("decisionId", out var did)
                                     && did.GetString() == id
                                     && d.TryGetProperty("resolved", out var r) && r.GetBoolean() == false));
        var payload = JsonSerializer.SerializeToElement(new
        {
            decisionId = AgeModelEngine.StableId("dec", $"{projectId}|{branch}|{decisionKind}|{targetId}|{accepted}|{note}|{expectedHeadSeq}"),
            projectId, decisionKind, targetId, accepted, note,
            conflictingDecisionIds = conflicts,
            resolved = !unresolvedConflict,
        });
        return Append(projectId, branch, expectedHeadSeq, EventTypes.DecisionRecorded, payload, actor);
    }

    public StoredEvent RecordRuleAssertion(string projectId, string branch, long expectedHeadSeq, RuleAssertion assertion, string actor = "system")
    {
        var payload = JsonSerializer.SerializeToElement(new { projectId, assertion });
        return Append(projectId, branch, expectedHeadSeq, EventTypes.RuleAssertion, payload, actor);
    }

    public StoredEvent FreezeModel(string projectId, string branch, long expectedHeadSeq, string modelId,
        IEnumerable<RuleAssertion>? assertions = null, string actor = "reviewer")
    {
        var state = Replay(projectId, branch, expectedHeadSeq);
        if (state.FrozenBranches.Contains(branch))
            throw new BranchFrozenException(branch);
        foreach (var a in assertions ?? Enumerable.Empty<RuleAssertion>())
        {
            var p = JsonSerializer.SerializeToElement(new { projectId, assertion = a });
            Append(projectId, branch, null, EventTypes.RuleAssertion, p, actor);
        }
        var payload = JsonSerializer.SerializeToElement(new { projectId, modelId, note = "模型冻结" });
        return Append(projectId, branch, expectedHeadSeq, EventTypes.ModelFrozen, payload, actor);
    }

    public StoredEvent ForkBranch(string projectId, string newBranch, string fromBranch, long atSeq, string reason, string actor = "reviewer")
    {
        var payload = JsonSerializer.SerializeToElement(new { projectId, newBranch, fromBranch, atSeq, reason });
        return Append(projectId, newBranch, null, EventTypes.BranchForked, payload, actor);
    }

    public StoredEvent RecordComparison(string projectId, string branch, long expectedHeadSeq,
        string frozenModelId, string newAnchorId, string deltaSummary, string actor = "reviewer")
    {
        var payload = JsonSerializer.SerializeToElement(new
        { projectId, frozenModelId, newAnchorId, deltaSummary });
        return Append(projectId, branch, expectedHeadSeq, EventTypes.ComparisonRecorded, payload, actor);
    }

    // ---------- 查询 ----------

    public ProjectState Replay(string projectId, string branch = "main", long? upToSeq = null)
    {
        var lineage = ResolveLineage(projectId, branch);
        var events = _store.GetEvents(projectId)
            .Where(e => lineage.Contains((e.Branch, e.Seq), BranchPointComparer.Instance))
            .OrderBy(e => e.Branch, StringComparer.Ordinal)
            .ThenBy(e => e.Seq)
            .ToList();

        var state = new ProjectState { ProjectId = projectId };
        foreach (var ev in events)
        {
            if (ev.Branch == branch && upToSeq is not null && ev.Seq > upToSeq.Value) break;
            Apply(ev, state);
            state.EventTimeline.Add(ev);
            if (ev.Branch == branch) { state.HeadSeq = ev.Seq; state.LastEvent = ev; }
        }
        return state;
    }

    private List<(string branch, long seq)> ResolveLineage(string projectId, string branch)
    {
        var all = _store.GetEvents(projectId);
        var pts = new List<(string branch, long seq)>();
        var current = branch;
        var startSeq = long.MaxValue;
        var guard = 0;
        while (guard++ < 64)
        {
            pts.AddRange(all.Where(e => e.Branch == current && e.Seq <= startSeq)
                            .Select(e => (current, e.Seq)));
            if (current == "main") break;
            var fork = all.LastOrDefault(e => e.Branch == current && e.Type == EventTypes.BranchForked);
            if (fork is null) break;
            current = fork.Payload.TryGetProperty("fromBranch", out var fb) ? fb.GetString() ?? "main" : "main";
            startSeq = fork.Payload.TryGetProperty("atSeq", out var a) ? a.GetInt64() : long.MaxValue;
        }
        return pts;
    }

    private sealed class BranchPointComparer : IEqualityComparer<(string, long)>
    {
        public static readonly BranchPointComparer Instance = new();
        public bool Equals((string, long) x, (string, long) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2;
        public int GetHashCode((string, long) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }

    public static List<RuleAssertion> EvaluateAssertions(ProjectState state)
    {
        var result = new List<RuleAssertion>();
        var m = state.Material;
        var d = state.LatestDerivation;
        if (m is not null)
        {
            // R2: 缺口/不整合必须作为显式段边界存在，而不是被插值跨越。
            var explicitBoundaries = m.Segments.Count(s => s.LowerBoundary != SegmentBoundaryType.Continuous);
            result.Add(new RuleAssertion(RuleAssertion.R2GapBoundary,
                "深度缺口与不整合是显式边界",
                m.Segments.Count > 1,
                explicitBoundaries > 0 ? "info" : "warning",
                m.Segments.Select(s => s.Id).ToList(),
                $"段数={m.Segments.Count}，非连续边界数={explicitBoundaries}"));

            // R3: 每个活跃锚点都有分布且分布样本被引擎消费。
            var active = m.Anchors.Where(a => !a.Excluded).ToList();
            var withDist = active.Count(a => a.AgeDistribution?.Samples.Length > 1);
            result.Add(new RuleAssertion(RuleAssertion.R3Distribution,
                "锚点分布不得被均值替代",
                withDist == active.Count && active.Count > 0,
                "info",
                active.Select(a => a.Id).ToList(),
                $"带完整分布的活跃锚点 {withDist}/{active.Count}"));
        }
        if (d is not null)
        {
            // R1: 不存在跨缺口/不整合的单调性约束；段内违例必须被标记无效。
            var violations = d.CandidateModels.Any(c => c.Valid && c.Violation is not null);
            var monoHolds = !d.Diagnostics.Any(x => x.Code == DiagnosticCodes.MonotonicityViolation) && !violations;
            result.Add(new RuleAssertion(RuleAssertion.R1Monotonic,
                "年龄只能在不断层段中单调",
                monoHolds,
                monoHolds ? "info" : "error",
                d.AffectedObjectIds.ToList(),
                monoHolds ? "所有不断层段内年龄单调" : "存在段内年龄倒置违例"));

            // R4: 等优模型全部保留。
            var ties = d.CandidateModels.Where(c => c.TieEquivalent && c.Valid).ToList();
            result.Add(new RuleAssertion(RuleAssertion.R4TieRetention,
                "多个等优模型全部保留",
                ties.Count != 1,
                ties.Count > 1 ? "info" : "info",
                ties.Select(c => c.ModelId).ToList(),
                ties.Count > 1 ? $"保留 {ties.Count} 个等优候选" : "当前无并列等优候选"));
        }
        return result;
    }

    public List<StoredEvent> GetTimeline(string projectId, string branch = "main")
        => _store.GetEvents(projectId).Where(e => e.Branch == branch).OrderBy(e => e.Seq).ToList();

    public IReadOnlyDictionary<string, (long seq, string? modelId)> FrozenBranches(string projectId)
        => _store.GetEvents(projectId)
            .Where(e => e.Type == EventTypes.ModelFrozen)
            .GroupBy(e => e.Branch)
            .ToDictionary(g => g.Key, g =>
            {
                var last = g.OrderBy(e => e.Seq).Last();
                var mid = last.Payload.TryGetProperty("modelId", out var v) ? v.GetString() : null;
                return (last.Seq, mid);
            });

    // ---------- 内部 ----------

    private StoredEvent Append(string projectId, string branch, long? expectedHeadSeq,
        string type, JsonElement payload, string actor,
        string? inputSummary = null, string? parameterVersion = null)
    {
        var currentHead = _store.HeadSeq(projectId, branch);
        if (expectedHeadSeq is not null && expectedHeadSeq.Value != currentHead)
            throw new ConcurrencyConflictException(expectedHeadSeq.Value, currentHead);

        var parent = _store.GetEvents(projectId).Where(e => e.Branch == branch)
            .OrderBy(e => e.Seq).LastOrDefault();
        var seq = currentHead + 1;
        var occurred = _clock.GetUtcNow().UtcDateTime.ToString("o");
        var parentHash = parent?.Hash;
        var canonical = $"{projectId}|{branch}|{seq}|{type}|{payload.GetRawText()}|{parentHash ?? ""}|{occurred}";
        var hash = "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32].ToLowerInvariant();
        var id = AgeModelEngine.StableId("ev", $"{projectId}|{branch}|{seq}|{type}|{parentHash ?? ""}");

        var ev = new StoredEvent
        {
            Id = id, Seq = seq, Type = type, Branch = branch,
            ParentEventId = parent?.Id, Payload = payload, Actor = actor,
            OccurredAt = occurred, InputSummary = inputSummary,
            ParameterVersion = parameterVersion, Hash = hash, ParentHash = parentHash,
        };
        _store.Append(ev);
        return ev;
    }

    private void EnsureNotFrozen(string projectId, string branch)
    {
        if (FrozenBranches(projectId).ContainsKey(branch))
            throw new BranchFrozenException(branch);
    }

    private string? LatestEventId(string projectId, string branch)
        => _store.GetEvents(projectId).Where(e => e.Branch == branch)
            .OrderBy(e => e.Seq).LastOrDefault()?.Id;

    private static void Apply(StoredEvent ev, ProjectState s)
    {
        switch (ev.Type)
        {
            case EventTypes.MaterialImported:
            case EventTypes.SourceAmended:
                if (ev.Payload.TryGetProperty("material", out var mat))
                {
                    var material = mat.Deserialize<CoreMaterial>()!;
                    s.Material = material;
                    s.MaterialVersions[ev.Id] = material;
                    s.CoreId = material.CoreId;
                }
                break;
            case EventTypes.ParameterRevised:
                if (ev.Payload.TryGetProperty("parameters", out var pp))
                {
                    var p = pp.Deserialize<EngineParameters>()!;
                    s.Parameters = p; s.ParameterVersion = p.Version;
                }
                break;
            case EventTypes.DerivationRecorded:
                if (ev.Payload.TryGetProperty("derivation", out var dd))
                    s.Derivations.Add(dd.Deserialize<DerivationResult>()!);
                break;
            case EventTypes.DecisionRecorded:
                s.Decisions.Add(ev.Payload);
                break;
            case EventTypes.ModelFrozen:
                s.FrozenBranches.Add(ev.Branch);
                if (ev.Payload.TryGetProperty("modelId", out var mid))
                { s.FrozenModelId = mid.GetString(); s.FrozenAtSeq = ev.Seq; }
                break;
        }
    }
}
