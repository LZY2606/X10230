using System.Text.Json;

namespace StratAge.Core;

/// <summary>
/// 项目状态：由事件流折叠得到。材料层（datum/segments/anchors/events/exclusions/sources）
/// 不可变；决定层（关联、排除、参数修订、冻结）以事件覆盖。任何时刻可回放到指定事件。
/// </summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string Branch { get; set; } = "main";

    public DepthDatum? Datum { get; set; }
    public readonly List<CoreSegment> Segments = new();
    public readonly List<AgeAnchor> Anchors = new();
    public readonly List<MarkerBed> Markers = new();
    public readonly List<ReservoirOffset> ReservoirOffsets = new();
    public readonly List<ExclusionDecision> Exclusions = new();
    public readonly List<SourceBundle> Sources = new();
    public readonly List<RecordedEvent> Events = new();

    public ParameterSet Parameters { get; set; } = new()
    {
        Version = "rules-v1.0",
        Label = "初始规则集 v1.0",
        Note = "连续段内强制单调；缺口/不整合为显式边界；保留分布与并列候选",
    };

    public IReadOnlyList<CandidateModel>? Candidates { get; set; }
    public string? DerivationEventId { get; set; }
    public string? FrozenAtEventId { get; set; }
    public readonly HashSet<string> FrozenBranches = new();
    public readonly Dictionary<string, string> MarkerDecision = new(); // markerId -> "anchorId|rejected|proposed"
    public readonly List<Diag> Diags = new();

    public bool IsFrozen => FrozenBranches.Contains(Branch);

    public IReadOnlyList<AgeAnchor> ActiveAnchors =>
        Anchors.Where(a => !a.Excluded).ToList();

    public CoreSegment? SegmentAt(double depthCm) =>
        Segments.FirstOrDefault(s =>
            s.TopDepth.Normalized is double t && s.BottomDepth.Normalized is double b &&
            depthCm >= Math.Min(t, b) - 1e-9 && depthCm <= Math.Max(t, b) + 1e-9);

    /// <summary>把事件折叠进状态（事件溯源 replay）。</summary>
    public void Apply(RecordedEvent e, JsonSerializerOptions opts)
    {
        Events.Add(e);
        switch (e.Type)
        {
            case EventTypes.ParameterRevision:
                Parameters = e.Payload.Deserialize<ParameterSet>(opts) ?? Parameters;
                break;
            case EventTypes.ManualDecision:
                ApplyManual(e, opts);
                break;
            case EventTypes.ExclusionRecorded:
            {
                var d = e.Payload.Deserialize<ExclusionDecision>(opts);
                if (d is not null) UpsertExclusion(d);
                break;
            }
            case EventTypes.CandidatesDerived:
            {
                var env = e.Payload.Deserialize<CandidateEnvelope>(opts);
                if (env is not null)
                {
                    Candidates = env.Candidates;
                    DerivationEventId = e.EventId;
                    Diags.Clear();
                    foreach (var d in env.Diags) Diags.Add(d);
                }
                break;
            }
            case EventTypes.ModelFrozen:
                FrozenBranches.Add(e.Branch);
                FrozenAtEventId = e.EventId;
                break;
            case EventTypes.CompareAnchor:
                break; // 比较结果存在事件 payload 中，不改主状态
        }
    }

    private void ApplyManual(RecordedEvent e, JsonSerializerOptions opts)
    {
        var el = e.Payload;
        if (el.TryGetProperty("kind", out var k) && k.GetString() == "marker-correlation")
        {
            var markerId = el.GetProperty("markerId").GetString()!;
            var action = el.GetProperty("action").GetString()!;
            var anchorId = el.TryGetProperty("anchorId", out var a) ? a.GetString() : null;
            MarkerDecision[markerId] = action == "adopt" ? $"adopt:{anchorId}"
                : action == "reject" ? "reject"
                : "propose";
            for (var i = 0; i < Markers.Count; i++)
            {
                var m = Markers[i];
                if (m.Id != markerId) continue;
                var corr = action == "adopt" ? EventCorrelation.Adopted
                    : action == "reject" ? EventCorrelation.Rejected : EventCorrelation.Proposed;
                Markers[i] = m with { Correlation = corr, DecidedAnchorId = anchorId };
            }
        }
    }

    public void UpsertExclusion(ExclusionDecision d)
    {
        Exclusions.RemoveAll(x => x.TargetObjectId == d.TargetObjectId);
        Exclusions.Add(d);
        for (var i = 0; i < Anchors.Count; i++)
            if (Anchors[i].Id == d.TargetObjectId && d.TargetKind == "anchor")
                Anchors[i] = Anchors[i] with { Excluded = true, ExclusionDecisionId = d.Id };
        for (var i = 0; i < Markers.Count; i++)
            if (Markers[i].Id == d.TargetObjectId && d.TargetKind == "marker")
                Markers[i] = Markers[i] with { Correlation = EventCorrelation.Rejected };
    }

    public void ClearExclusion(string targetId)
    {
        Exclusions.RemoveAll(x => x.TargetObjectId == targetId);
        for (var i = 0; i < Anchors.Count; i++)
            if (Anchors[i].Id == targetId)
                Anchors[i] = Anchors[i] with { Excluded = false, ExclusionDecisionId = null };
    }

    /// <summary>只接受同一分支且序号不晚于 cutoff 的事件，恢复历史可见结论。</summary>
    public static ProjectState Replay(IEnumerable<RecordedEvent> all, string branch,
        string? cutoffEventId, JsonSerializerOptions opts)
    {
        var st = new ProjectState { Branch = branch };
        RecordedEvent? cutoff = null;
        foreach (var e in all.Where(x => x.Branch == branch).OrderBy(x => x.Sequence))
        {
            if (cutoffEventId is not null && e.EventId == cutoffEventId) { cutoff = e; break; }
            st.Apply(e, opts);
            if (cutoffEventId is not null && string.Equals(e.EventId, cutoffEventId, StringComparison.Ordinal))
                break;
        }
        if (cutoffEventId is not null && cutoff is null)
            throw new InvalidOperationException($"历史事件不存在: {cutoffEventId}");
        return st;
    }
}

public sealed record CandidateEnvelope(
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diag> Diags,
    string ParameterVersion,
    string InputDigest,
    string? ParentDerivationEventId,
    int DistributionSamples,
    string BudgetStatus);
