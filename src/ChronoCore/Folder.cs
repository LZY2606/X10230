using System.Text.Json;

namespace ChronoCore;

public static class EventFolder
{
    private static readonly JsonSerializerOptions O =
        new() { PropertyNameCaseInsensitive = true };

    public static ProjectState FoldTo(IEnumerable<ChronoEvent> events, string branchId, long? toSeq)
    {
        var ordered = events.OrderBy(e => e.Seq).ToList();
        var state = new ProjectState { BranchId = branchId };
        // main 分支只读自身；反例分支在 fork 点继承父分支全部事件，再叠加本分支事件
        var relevant = new List<ChronoEvent>();
        if (branchId != "main")
        {
            var fork = ordered.LastOrDefault(e =>
                e.Type == EventType.BranchForked && e.BranchId == branchId);
            var fp = fork?.Payload.Deserialize<BranchForkedPayload>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var parent = fp?.ParentBranchId ?? "main";
            var forkAt = fork?.At ?? "";
            relevant.AddRange(ordered.Where(e => e.BranchId == parent &&
                string.CompareOrdinal(e.At, forkAt) <= 0));
        }
        relevant.AddRange(ordered.Where(e => e.BranchId == branchId));
        if (toSeq is not null)
        {
            var forkAt = relevant.FirstOrDefault(e =>
                e.Type == EventType.BranchForked && e.BranchId == branchId)?.At;
            relevant = relevant.Where(e =>
                e.BranchId != branchId || e.Seq <= toSeq.Value).ToList();
        }
        relevant = relevant.OrderBy(e => e.At).ToList();

        foreach (var ev in relevant) Apply(ev, state);
        state.HeadSeq = relevant.Where(e => e.BranchId == branchId)
            .Select(e => e.Seq).DefaultIfEmpty(0).Max();
        return state;
    }

    public static void Apply(ChronoEvent ev, ProjectState s)
    {
        switch (ev.Type)
        {
            case EventType.ProjectCreated:
            {
                var p = ev.Payload.Deserialize<ProjectCreatedPayload>(O)!;
                s.ProjectId = ev.BranchId == "main" ? "proj-core7b" : s.ProjectId;
                s.Name = p.Name; s.BranchId = ev.BranchId;
                break;
            }
            case EventType.MaterialImported:
            {
                var p = ev.Payload.Deserialize<MaterialImportedPayload>(O)!;
                Put(s, p.Kind, p.MaterialId, p.RawJson);
                break;
            }
            case EventType.MaterialRevised:
            {
                var p = ev.Payload.Deserialize<MaterialRevisedPayload>(O)!;
                if (p.Kind == "parameter-set" || p.Kind == "parameter")
                {
                    var ps = JsonSerializer.Deserialize<ParameterSet>(p.NewRawJson, O)!;
                    s.Parameters[ps.Version] = ps;
                    var idx = s.ParameterOrder.FindIndex(x => x.Version == ps.Version);
                    if (idx >= 0) s.ParameterOrder[idx] = ps; else s.ParameterOrder.Add(ps);
                    s.CurrentParameterVersion = ps.Version;
                    break;
                }
                Put(s, p.Kind, p.MaterialId, p.NewRawJson);
                break;
            }
            case EventType.MaterialExcluded:
            {
                var p = ev.Payload.Deserialize<MaterialExcludedPayload>(O)!;
                s.Exclusions[p.Exclusion.Id] = p.Exclusion;
                if (!s.ExclusionOrder.Any(x => x.Id == p.Exclusion.Id)) s.ExclusionOrder.Add(p.Exclusion);
                break;
            }
            case EventType.ParameterRevised:
            {
                var p = ev.Payload.Deserialize<ParameterRevisedPayload>(O)!;
                s.Parameters[p.Parameters.Version] = p.Parameters;
                if (!s.ParameterOrder.Any(x => x.Version == p.Parameters.Version))
                    s.ParameterOrder.Add(p.Parameters);
                s.CurrentParameterVersion = p.Parameters.Version;
                break;
            }
            case EventType.DecisionMade:
            {
                var p = ev.Payload.Deserialize<DecisionPayload>(O)!;
                s.Decisions[p.Decision.Id] = p.Decision;
                s.DecisionOrder.Add(p.Decision);
                break;
            }
            case EventType.ModelsDerived:
            {
                var p = ev.Payload.Deserialize<ModelsDerivedPayload>(O)!;
                foreach (var id in p.ModelIds)
                    if (!s.Derived.ContainsKey(id))
                        s.Derived[id] = PlaceholderModel(id, p);
                break;
            }
            case EventType.LocalRecompute:
            {
                var p = ev.Payload.Deserialize<ModelsDerivedPayload>(O)!;
                foreach (var id in p.ModelIds)
                    if (!s.Derived.ContainsKey(id)) s.Derived[id] = PlaceholderModel(id, p);
                break;
            }
            case EventType.ModelFrozen:
            {
                var p = ev.Payload.Deserialize<ModelFrozenPayload>(O)!;
                s.FrozenModelIds.Add(p.ModelId);
                break;
            }
            case EventType.BranchForked:
            {
                var p = ev.Payload.Deserialize<BranchForkedPayload>(O)!;
                s.BaselineDigest = p.BaselineDigest; s.ChangedSourceId = p.ChangedSourceId;
                break;
            }
        }
    }

    private static DerivedModel PlaceholderModel(string id, ModelsDerivedPayload p)
        => new(id, "fam-" + p.InputDigest[..8], id, 0, 0, false, Array.Empty<string>(),
            Array.Empty<RateSegment>(), Array.Empty<EnvelopePoint>(), Array.Empty<Diagnostic>(),
            p.InputDigest, p.ParameterVersion, Array.Empty<string>(),
            p.OutcomeKind == nameof(OutcomeKind.Success),
            $"事件折叠占位，完整模型以 {id} 派生快照为准");

    private static void Put(ProjectState s, string kind, string id, string json)
    {
        switch (kind)
        {
            case "datum": Add(s.Datums, s.DatumOrder, id, json, () => JsonSerializer.Deserialize<DepthDatum>(json, O)!); break;
            case "interval": Add(s.Intervals, s.IntervalOrder, id, json, () => JsonSerializer.Deserialize<CoreInterval>(json, O)!); break;
            case "gap": Add(s.Gaps, s.GapOrder, id, json, () => JsonSerializer.Deserialize<GapBoundary>(json, O)!); break;
            case "reservoir": Add(s.Reservoirs, s.ReservoirOrder, id, json, () => JsonSerializer.Deserialize<ReservoirOffset>(json, O)!); break;
            case "event": Add(s.Events, s.EventOrder, id, json, () => JsonSerializer.Deserialize<EventLayer>(json, O)!); break;
            case "anchor": Add(s.Anchors, s.AnchorOrder, id, json, () => JsonSerializer.Deserialize<Anchor>(json, O)!); break;
            default: throw new InvalidOperationException($"未知材料种类 '{kind}'（格式损坏保护）");
        }
    }

    private static void Add<T>(Dictionary<string, T> map, List<T> order, string id, string json, Func<T> read)
    {
        var item = read();
        map[id] = item;
        var idx = order.FindIndex(x => IdOf(x) == id);
        if (idx >= 0) order[idx] = item; else order.Add(item);
    }

    private static string IdOf(object o) => o switch
    {
        DepthDatum m => m.Id, CoreInterval m => m.Id, GapBoundary m => m.Id, Anchor m => m.Id,
        ReservoirOffset m => m.Id, EventLayer m => m.Id, _ => throw new InvalidOperationException()
    };
}
