using AgeDepthStudio.Domain;
using AgeDepthStudio.Store;

namespace AgeDepthStudio.Engine;

public sealed record TrialOverride(string Kind, string Id, string? DepthRaw, double[]? Samples,
    string? SegmentId = null, int? Budget = null);
public sealed record TrialRequest(string TrialId, string DirectSourceId, string Mutation,
    TrialOverride Override, string? ExpectedUnconvertedNote = null);

/// <summary>应用服务：所有业务规则在服务端执行，测试可直接调用，不依赖浏览器。</summary>
public sealed class StudioService
{
    private readonly EventStore _store;
    private readonly ModelEngine _engine = new();

    public StudioService(EventStore store) => _store = store;

    public EventStore Store => _store;

    public ProjectedState State(string ws, long? upToSeq = null) =>
        Projector.Project(_store.Events(ws, upToSeq));

    public void CreateWorkspace(string ws, string name, string kind)
    {
        _store.CreateWorkspace(ws, name, kind);
        if (_store.Events(ws).Count == 0)
            _store.Append("workspace.created", ws, "system", JsonSerializer.Serialize(new { ws, name, kind }));
    }

    public StudioEvent Emit(string ws, string type, object payload, string actor = "system", string? id = null)
        => _store.Append(type, ws, actor, JsonSerializer.Serialize(payload), id);

    public StudioEvent RegisterProject(string ws, CoreProject p, string actor = "system")
        => Emit(ws, "project.registered", p, actor);
    public StudioEvent AddSegment(string ws, CoreSegment x, string actor = "system")
    {
        if (!Depth.TryParseMeters(x.TopDepthRaw, out _))
            Emit(ws, "import.diagnostic", new Diagnostic("SEGMENT_DEPTH_UNCONVERTIBLE", "error",
                $"top='{x.TopDepthRaw}' 无法转换，原值保留", x.SegmentId, "/topDepthRaw"), actor);
        return Emit(ws, "segment.added", x, actor);
    }
    public StudioEvent AddGap(string ws, SegmentGap x, string actor = "system") => Emit(ws, "gap.added", x, actor);
    public StudioEvent AddAnchor(string ws, AgeAnchor x, string actor = "system")
    {
        if (!Depth.TryParseMeters(x.DepthRaw, out _))
            Emit(ws, "import.diagnostic", new Diagnostic("ANCHOR_DEPTH_UNCONVERTIBLE", "error",
                $"depth='{x.DepthRaw}' 无法转换，原值保留且不为绘图丢弃", x.AnchorId, "/depthRaw"), actor);
        return Emit(ws, "anchor.added", x, actor);
    }
    public StudioEvent AddOffset(string ws, ReservoirOffset x, string actor = "system") => Emit(ws, "offset.added", x, actor);
    public StudioEvent AddLayer(string ws, EventLayer x, string actor = "system") => Emit(ws, "layer.added", x, actor);
    public StudioEvent RecordDecision(string ws, Decision d, string actor = "system")
    {
        var s = State(ws);
        // 与既有决定冲突时，两者都保留，另发结构化冲突事件。
        var clash = s.Decisions
            .Where(x => x.TargetId == d.TargetId && x.Action != d.Action)
            .OrderBy(x => x.DecisionId, StringComparer.Ordinal).LastOrDefault();
        var e = Emit(ws, "decision.recorded", d, actor, d.DecisionId);
        if (clash is not null)
            Emit(ws, "decision.conflict", new
            {
                rule = "conflicting-human-decisions-retained",
                targetId = d.TargetId,
                priorDecisionId = clash.DecisionId,
                newDecisionId = d.DecisionId,
                basis = "互相冲突的人工决定均保留，由后续重算体现，不静默覆盖"
            }, actor);
        return e;
    }

    public RuleParams IssueParams(string ws, RuleParams p, bool activate, string actor = "system")
    {
        Emit(ws, "params.issued", p, actor, p.ParamsId);
        if (activate) Emit(ws, "params.activated", new { paramsId = p.ParamsId, version = p.Version }, actor);
        return p;
    }

    /// <summary>重算。affectedSegmentIds 为空表示全工作区；否则为局部重算。</summary>
    public StudioEvent Recompute(string ws, string modelId, IReadOnlyList<string>? affectedSegmentIds = null,
        string triggerEventId = "", string actor = "engine")
    {
        var s = State(ws);
        var p = s.ActiveParams;
        var prev = s.Models.LastOrDefault();
        var outp = _engine.Compute(s, p, modelId, triggerEventId);
        var segInputs = s.Segments.ToDictionary(seg => seg.SegmentId, seg => ModelEngine.SegmentFingerprint(s, p, seg.SegmentId));
        var payload = new
        {
            result = outp.Result,
            paramsId = p.ParamsId,
            paramsVersion = p.Version,
            inputFingerprint = outp.Result.InputFingerprint,
            triggerEventId,
            parentModelId = prev?.ModelId,
            affectedSegmentIds = affectedSegmentIds ?? s.Segments.Select(x => x.SegmentId).ToArray(),
            localOnly = affectedSegmentIds is not null,
            segmentInputs = segInputs,
            inputSummary = new
            {
                segments = s.Segments.Count,
                gaps = s.Gaps.Count,
                anchorIds = s.Anchors.Select(a => a.AnchorId).ToArray(),
                offsetIds = s.Offsets.Select(a => a.OffsetId).ToArray(),
                layerIds = s.Layers.Select(a => a.LayerId).ToArray(),
                decisionIds = s.Decisions.Select(a => a.DecisionId).ToArray()
            }
        };
        return Emit(ws, "model.recomputed", payload, actor, null);
    }

    /// <summary>直接来源（锚点）修订：只重算依赖它的段，其余段候选身份保持不变。</summary>
    public StudioEvent ReviseAnchor(string ws, AgeAnchor revised, string reason, string actor = "sed-team")
    {
        var before = State(ws);
        var original = before.Anchors.First(a => a.AnchorId == revised.AnchorId);
        var re = Emit(ws, "anchor.revised", new
        {
            anchor = revised,
            originalSnippet = original,
            reason,
            basis = "不可变材料留痕：原始片段保存在本事件中，状态仅持有当前版本"
        }, actor);
        return Recompute(ws, NextModelId(ws), new[] { revised.SegmentId }, re.Id, actor);
    }

    /// <summary>
    /// 反例试验：克隆状态后只改动一个直接来源，比较影响范围。试验不破坏基线；
    /// adopted=true 时把修订落实为真实事件并局部重算。
    /// </summary>
    public ArchiveTrial RunTrial(string ws, TrialRequest req, bool adopt, string actor = "reviewer")
    {
        var before = State(ws);
        var p = before.ActiveParams;
        var cloned = Clone(before);
        ApplyOverride(cloned, req.Override);
        var fpBefore = before.Segments.ToDictionary(x => x.SegmentId, x => ModelEngine.SegmentFingerprint(before, p, x.SegmentId));
        var fpAfter = cloned.Segments.ToDictionary(x => x.SegmentId, x => ModelEngine.SegmentFingerprint(cloned, p, x.SegmentId));
        var affected = fpBefore.Keys.Where(k => fpBefore[k] != fpAfter[k]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var outp = _engine.Compute(cloned, p, "trial_model", "");
        var sourceSeg = req.Override.Kind switch
        {
            "anchor" => cloned.Anchors.First(a => a.AnchorId == req.Override.Id).SegmentId,
            "anchor-add" => req.Override.SegmentId!,
            "segment" => req.Override.Id,
            "params" => "",
            "gap" => before.Gaps.First(g => g.GapId == req.Override.Id).AboveSegmentId + "," +
                      before.Gaps.First(g => g.GapId == req.Override.Id).BelowSegmentId,
            _ => ""
        };
        var expected = sourceSeg.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var scope = expected.Length == 0
            ? (affected.Length == 0 ? "Local" : "WorkspaceWide")
            : (affected.All(a => expected.Contains(a)) && affected.Length <= expected.Length ? "Local" : "WorkspaceWide");
        var payload = new
        {
            trialId = req.TrialId,
            directSourceId = req.DirectSourceId,
            mutation = req.Mutation,
            modelId = "trial_model",
            outcome = outp.Result.OverallOutcome.ToString(),
            affectedSegmentIds = affected,
            impactScope = scope,
            adopted = adopt,
            judgmentEvents = outp.Judgments,
            candidatesBySegment = outp.Result.Segments.ToDictionary(x => x.SegmentId, x => new
            {
                x.Outcome,
                candidateIds = x.Candidates.Select(c => c.CandidateId).ToArray(),
                diagnostics = x.Diagnostics.Select(d => new { d.Code, d.Message, d.JsonPointer })
            })
        };
        var e = Emit(ws, "trial.result", payload, actor);
        if (adopt && req.Override.Kind is "anchor")
        {
            var a = cloned.Anchors.First(x => x.AnchorId == req.Override.Id);
            ReviseAnchor(ws, a, $"采纳试验 {req.TrialId} 的单一来源修订", actor);
            Emit(ws, "trial.adoption", new { req.TrialId, adopted = true }, actor);
        }
        else if (!adopt)
        {
            Emit(ws, "trial.adoption", new { req.TrialId, adopted = false, basis = "反例结果保留但不采纳，基线不被覆盖" }, actor);
        }
        return new ArchiveTrial(req.TrialId, req.DirectSourceId, req.Mutation, "trial_model",
            outp.Result.OverallOutcome, affected, scope, adopt, e.Id, e.At);
    }

    public bool TryFreeze(string ws, string modelId, string expectedParamsId, string actor, out StudioEvent? conflict)
        => _store.TryFreeze(ws, modelId, expectedParamsId, actor, out conflict);

    public static ProjectedState Clone(ProjectedState s) => s.DeepClone();

    private static void ApplyOverride(ProjectedState s, TrialOverride o)
    {
        switch (o.Kind)
        {
            case "anchor":
            {
                var idx = s.Anchors.FindIndex(a => a.AnchorId == o.Id);
                var cur = s.Anchors[idx];
                s.Anchors[idx] = cur with
                {
                    DepthRaw = o.DepthRaw ?? cur.DepthRaw,
                    Samples = o.Samples ?? cur.Samples
                };
                break;
            }
            case "anchor-add":
            {
                if (s.Anchors.Any(a => a.AnchorId == o.Id))
                {
                    var idx0 = s.Anchors.FindIndex(a => a.AnchorId == o.Id);
                    var cur0 = s.Anchors[idx0];
                    s.Anchors[idx0] = cur0 with { DepthRaw = o.DepthRaw ?? cur0.DepthRaw, Samples = o.Samples ?? cur0.Samples };
                }
                else
                {
                    // 冻结后的“新锚点”试验：只存在于克隆状态，基线不被改动。
                    s.Anchors.Add(new AgeAnchor(o.Id, o.SegmentId!, o.DepthRaw!, "TRIAL", "trial-observation",
                        o.Samples ?? Array.Empty<double>(), "非破坏试验锚点"));
                }
                break;
            }
            case "params":
            {
                var cur = s.ActiveParams;
                var np = cur with { EnumerationBudget = o.Budget ?? cur.EnumerationBudget };
                s.Params.Add(np);
                s.ActiveParamsId = np.ParamsId;
                break;
            }
            case "gap":
            {
                var idx = s.Gaps.FindIndex(g => g.GapId == o.Id);
                var cur = s.Gaps[idx];
                s.Gaps[idx] = cur with { Kind = GapKind.Unconformity };
                break;
            }
            case "segment":
            {
                var idx = s.Segments.FindIndex(g => g.SegmentId == o.Id);
                var cur = s.Segments[idx];
                s.Segments[idx] = cur with { TopDepthRaw = o.DepthRaw ?? cur.TopDepthRaw };
                break;
            }
        }
    }

    public string NextModelId(string ws) => $"M{State(ws).Models.Count + 1}";
}
