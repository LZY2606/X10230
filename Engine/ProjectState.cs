using System.Text.Json;
using CoreChrono.Domain;

namespace CoreChrono.Engine;

public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";

    public Dictionary<string, DepthDatum> Datums { get; } = new();
    public Dictionary<string, Section> Sections { get; } = new();
    public Dictionary<string, Boundary> Boundaries { get; } = new();
    public Dictionary<string, ControlPoint> Controls { get; } = new();
    public Dictionary<string, ReservoirOffset> Reservoirs { get; } = new();
    public Dictionary<string, EventBed> EventBeds { get; } = new();
    public Dictionary<string, Exclusion> Exclusions { get; } = new();
    public Dictionary<string, ParameterSet> ParameterSets { get; } = new();
    public Dictionary<string, DecisionRecord> Decisions { get; } = new();
    public Dictionary<string, FreezeRecord> Freezes { get; } = new();
    public Dictionary<string, DerivedNode> Nodes { get; } = new();
    public List<string> EventIds { get; } = new();
    public HashSet<string> Branches { get; } = new() { "main" };
    public string? BaselineNodeId { get; set; }
    public string CurrentParameterVersion { get; set; } = "";

    public IEnumerable<ControlPoint> EligibleControls => Controls.Values
        .Where(c => !c.Excluded && Exclusions.Values.All(e => e.TargetId != c.Id));

    public ParameterSet CurrentParameters =>
        ParameterSets.TryGetValue(CurrentParameterVersion, out var ps) ? ps : ParameterSets.Values.OrderBy(p => p.Id).First();

    public static ProjectState Replay(string projectId, IEnumerable<JournalEvent> events)
    {
        var st = new ProjectState { ProjectId = projectId };
        foreach (var ev in events.OrderBy(e => e.Seq)) Apply(st, ev);
        return st;
    }

    public static void Apply(ProjectState st, JournalEvent ev)
    {
        if (st.ProjectId == "") st.ProjectId = ev.ProjectId;
        st.EventIds.Add(ev.EventId);
        st.Branches.Add(ev.Branch);
        switch (ev.Type)
        {
            case EventType.ProjectCreated:
                st.Name = ev.Payload.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                st.CreatedAt = ev.Payload.TryGetProperty("createdAt", out var c) ? c.GetString() ?? "" : "";
                break;
            case EventType.MaterialImported:
                ImportUtil.Merge(st, ev.Payload);
                break;
            case EventType.ExclusionRecorded:
                var ex = ev.Payload.Deserialize<Exclusion>(Support.Json) ?? throw new InvalidOperationException();
                st.Exclusions[ex.Id] = ex;
                if (ex.TargetKind == RefKind.Control && st.Controls.TryGetValue(ex.TargetId, out var cp))
                    st.Controls[cp.Id] = cp with { Excluded = true, ExclusionId = ex.Id };
                break;
            case EventType.ParameterSetIssued:
                var ps = ev.Payload.Deserialize<ParameterSet>(Support.Json)!;
                st.ParameterSets[ps.Id] = ps;
                st.CurrentParameterVersion = ps.Id;
                break;
            case EventType.ModelDerived:
                var nodes = ev.Payload.Deserialize<List<DerivedNode>>(Support.Json) ?? new();
                foreach (var node in nodes) st.Nodes[node.NodeId] = node;
                break;
            case EventType.DecisionRecorded:
                var d = ev.Payload.Deserialize<DecisionRecord>(Support.Json)!;
                st.Decisions[d.DecisionId] = d;
                if (d is { Conflicting: false, Action: DecisionAction.Accept } && d.CandidateId is not null)
                {
                    foreach (var prior in st.Decisions.Values
                                 .Where(x => !x.Conflicting && x.Action == DecisionAction.Accept
                                         && x.CandidateId is not null && x.CandidateId != d.CandidateId
                                         && x.Branch == d.Branch && x.SupersedesDecisionId is null))
                    {
                        prior.SupersedesDecisionId = prior.DecisionId;
                    }
                }
                break;
            case EventType.ModelFrozen:
                var f = ev.Payload.Deserialize<FreezeRecord>(Support.Json)!;
                st.Freezes[f.FreezeId] = f;
                break;
            case EventType.FreezeRejected:
                var fr = ev.Payload.Deserialize<FreezeRecord>(Support.Json)!;
                st.Freezes[fr.FreezeId] = fr;
                break;
            case EventType.BaselineCaptured:
                st.BaselineNodeId = ev.Payload.TryGetProperty("nodeId", out var bn) ? bn.GetString() : null;
                break;
        }
    }
}

internal static class ImportUtil
{
    public static void Merge(ProjectState st, JsonElement payload)
    {
        foreach (var (key, target) in new (string, Action<JsonElement>)[]
        {
            ("datums", e => st.Datums[GetId(e)] = e.Deserialize<DepthDatum>(Support.Json)!),
            ("sections", e => st.Sections[GetId(e)] = e.Deserialize<Section>(Support.Json)!),
            ("boundaries", e => st.Boundaries[GetId(e)] = e.Deserialize<Boundary>(Support.Json)!),
            ("controls", e =>
            {
                var cp = e.Deserialize<ControlPoint>(Support.Json)!;
                if (st.Exclusions.Values.Any(x => x.TargetId == cp.Id)) cp = cp with { Excluded = true };
                st.Controls[cp.Id] = cp;
            }),
            ("reservoirs", e => st.Reservoirs[GetId(e)] = e.Deserialize<ReservoirOffset>(Support.Json)!),
            ("eventBeds", e => st.EventBeds[GetId(e)] = e.Deserialize<EventBed>(Support.Json)!),
            ("exclusions", e =>
            {
                var ex = e.Deserialize<Exclusion>(Support.Json)!;
                st.Exclusions[ex.Id] = ex;
                if (ex.TargetKind == RefKind.Control && st.Controls.TryGetValue(ex.TargetId, out var cp))
                    st.Controls[cp.Id] = cp with { Excluded = true, ExclusionId = ex.Id };
            }),
            ("parameterSets", e =>
            {
                var ps = e.Deserialize<ParameterSet>(Support.Json)!;
                st.ParameterSets[ps.Id] = ps;
                st.CurrentParameterVersion = ps.Id;
            })
        })
        {
            if (payload.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray()) target(item);
        }
    }

    private static string GetId(JsonElement e) => e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
}
