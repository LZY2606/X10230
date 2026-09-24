using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Store;

/// <summary>
/// 事件 -> 状态投影。回到任意旧事件重放即可恢复当时的依赖版本与可见结论；
/// 不会拿最新计算结果覆盖历史。
/// </summary>
public static class Projector
{
    private static readonly JsonSerializerOptions O = new() { PropertyNameCaseInsensitive = true };

    public static ProjectedState Project(IReadOnlyList<StudioEvent> events)
    {
        var s = new ProjectedState();
        foreach (var e in events) Apply(s, e);
        return s;
    }

    private static void Apply(ProjectedState s, StudioEvent e)
    {
        var p = e.PayloadJson;
        switch (e.Type)
        {
            case "project.registered":
                s.Project = JsonSerializer.Deserialize<CoreProject>(p, O);
                break;
            case "segment.added":
                Add(s.Segments, JsonSerializer.Deserialize<CoreSegment>(p, O)!);
                break;
            case "gap.added":
                Add(s.Gaps, JsonSerializer.Deserialize<SegmentGap>(p, O)!);
                break;
            case "anchor.added":
                Add(s.Anchors, JsonSerializer.Deserialize<AgeAnchor>(p, O)!);
                break;
            case "anchor.revised":
            {
                // 直接来源修订：原始片段保留在事件里，状态只持有当前版本。
                var node = JsonSerializer.Deserialize<JsonElement>(p);
                var revised = node.GetProperty("anchor").Deserialize<AgeAnchor>(O)!;
                Add(s.Anchors, revised);
                break;
            }
            case "offset.added":
                Add(s.Offsets, JsonSerializer.Deserialize<ReservoirOffset>(p, O)!);
                break;
            case "layer.added":
                Add(s.Layers, JsonSerializer.Deserialize<EventLayer>(p, O)!);
                break;
            case "params.issued":
                Add(s.Params, JsonSerializer.Deserialize<RuleParams>(p, O)!);
                break;
            case "params.activated":
                s.ActiveParamsId = JsonSerializer.Deserialize<JsonElement>(p).GetProperty("paramsId").GetString();
                break;
            case "decision.recorded":
                Add(s.Decisions, JsonSerializer.Deserialize<Decision>(p, O)!);
                break;
            case "model.recomputed":
            {
                var node = JsonSerializer.Deserialize<JsonElement>(p);
                var model = node.GetProperty("result").Deserialize<ModelResult>(O)!;
                Add(s.Models, model);
                break;
            }
            case "model.frozen":
            {
                var node = JsonSerializer.Deserialize<JsonElement>(p);
                s.Freeze = new FreezePointer(
                    node.GetProperty("modelId").GetString()!,
                    e.At, e.Actor, node.GetProperty("token").GetString()!);
                break;
            }
            case "archive.baseline_established":
                s.BaselineModelId = JsonSerializer.Deserialize<JsonElement>(p).GetProperty("modelId").GetString();
                break;
            case "trial.result":
            {
                var t = DeserializeTrial(JsonSerializer.Deserialize<JsonElement>(p), e);
                Add(s.Trials, t);
                break;
            }
            case "trial.adoption":
            {
                var node = JsonSerializer.Deserialize<JsonElement>(p);
                var id = node.GetProperty("trialId").GetString()!;
                var adopted = node.GetProperty("adopted").GetBoolean();
                var idx = s.Trials.FindIndex(t => t.TrialId == id);
                if (idx >= 0)
                {
                    var old = s.Trials[idx];
                    s.Trials[idx] = old with { Adopted = adopted };
                }
                break;
            }
        }
    }

    private static ArchiveTrial DeserializeTrial(JsonElement node, StudioEvent e)
    {
        var affected = node.GetProperty("affectedSegmentIds").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        return new ArchiveTrial(
            node.GetProperty("trialId").GetString()!,
            node.GetProperty("directSourceId").GetString()!,
            node.GetProperty("mutation").GetString()!,
            node.GetProperty("modelId").GetString()!,
            Enum.Parse<OutcomeKind>(node.GetProperty("outcome").GetString()!),
            affected,
            node.GetProperty("impactScope").GetString()!,
            node.TryGetProperty("adopted", out var a) && a.GetBoolean(),
            e.Id,
            e.At);
    }

    private static void Add<T>(List<T> list, T item) where T : class
    {
        var idProp = typeof(T).GetProperty(item switch
        {
            CoreSegment => "SegmentId", SegmentGap => "GapId", AgeAnchor => "AnchorId",
            ReservoirOffset => "OffsetId", EventLayer => "LayerId", RuleParams => "ParamsId",
            Decision => "DecisionId", ModelResult => "ModelId", ArchiveTrial => "TrialId",
            _ => throw new ArgumentException(typeof(T).Name)
        });
        var id = (string)idProp!.GetValue(item)!;
        list.RemoveAll(x => (string)idProp.GetValue(x)! == id);
        list.Add(item);
    }
}
