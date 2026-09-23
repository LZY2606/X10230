using System.Text.Json;

namespace CoreChronology;

/// <summary>Folded view of a branch: evidence layer + decision layer, kept separate.</summary>
public sealed class ProjectState
{
    public string ProjectId { get; private set; } = "";
    public string CoreLabel { get; private set; } = "";
    public string CoordinateBasis { get; private set; } = "";
    public string DepthDatumId { get; private set; } = "";
    public bool Frozen { get; private set; }
    public string? FrozenModelHash { get; private set; }
    public ModelParameters Parameters { get; private set; } = ModelParameters.V1;

    private readonly Dictionary<string, Material> _current = new();
    private readonly Dictionary<string, List<MaterialVersionRow>> _versions = new();
    private readonly List<ProjectEvent> _events = new();
    private readonly Dictionary<string, Exclusion> _exclusions = new();
    private readonly List<ManualDecisionPayload> _decisions = new();
    private readonly List<ImportDiagnostic> _importDiagnostics = new();

    public sealed record MaterialVersionRow(string EventId, int Version, Material Material);

    public IReadOnlyDictionary<string, Material> CurrentMaterials => _current;
    public IReadOnlyList<ProjectEvent> Events => _events;
    public IReadOnlyCollection<Exclusion> Exclusions => _exclusions.Values;
    public IReadOnlyList<ManualDecisionPayload> Decisions => _decisions;
    public IReadOnlyList<ImportDiagnostic> ImportDiagnostics => _importDiagnostics;
    public IEnumerable<MaterialVersionRow> VersionsOf(string logicalId) =>
        _versions.TryGetValue(logicalId, out var v) ? v : Array.Empty<MaterialVersionRow>();

    public string TipEventId => _events.Count == 0 ? "" : _events[^1].Id;
    public long TipSeq => _events.Count == 0 ? 0 : _events[^1].Seq;

    /// <summary>Digest of exactly the evidence the derived layer will consume.</summary>
    public string EvidenceDigest()
    {
        var ordered = _current.Keys.OrderBy(k => k, StringComparer.Ordinal);
        var parts = new List<string> { DepthDatumId, Parameters.Version.ToString() };
        foreach (var id in ordered)
        {
            var m = _current[id];
            parts.Add($"{id}={m.GetType().Name}:{m.SourceRef}#{m.SourceLine}");
        }
        parts.Add("excl:" + string.Join(",", _exclusions.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        return DeterministicRng.StableHash(string.Join("|", parts)).ToString("x16");
    }

    public IEnumerable<T> MaterialsOfType<T>() where T : Material => _current.Values.OfType<T>();
    public Material? Material(string id) => _current.GetValueOrDefault(id);
    public bool IsExcluded(string id) => _exclusions.ContainsKey(id);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Pure fold: returns a new state advanced by one event. Frozen state refuses mutation.</summary>
    public ProjectState Apply(ProjectEvent e)
    {
        var next = Clone();
        next._events.Add(e);
        switch (e.Kind)
        {
            case EventKind.ProjectCreated:
                var pc = JsonSerializer.Deserialize<ProjectCreatedPayload>(e.PayloadJson, JsonOptions)!;
                if (next.Frozen) throw new InvalidOperationException("frozen");
                next.ProjectId = pc.ProjectId;
                next.CoreLabel = pc.CoreLabel;
                next.DepthDatumId = pc.DepthDatumId;
                next.CoordinateBasis = pc.CoordinateBasis;
                break;

            case EventKind.MaterialsCommitted:
                if (next.Frozen) throw new BranchFrozenException(e.BranchId);
                var mc = JsonSerializer.Deserialize<MaterialsCommittedPayload>(e.PayloadJson, JsonOptions)!;
                foreach (var mv in mc.Versions)
                {
                    var mat = MaterialJson.Read(mv.Kind, mv.MaterialJson);
                    next._current[mv.LogicalId] = mat;
                    if (!next._versions.TryGetValue(mv.LogicalId, out var list))
                        next._versions[mv.LogicalId] = list = new();
                    list.Add(new MaterialVersionRow(e.Id, mv.Version, mat));
                }
                next._importDiagnostics.AddRange(mc.Diagnostics);
                break;

            case EventKind.ParameterRevision:
                if (next.Frozen) throw new BranchFrozenException(e.BranchId);
                var pr = JsonSerializer.Deserialize<ParameterRevisionPayload>(e.PayloadJson, JsonOptions)!;
                next.Parameters = pr.Parameters;
                break;

            case EventKind.ExclusionDecided:
                if (next.Frozen) throw new BranchFrozenException(e.BranchId);
                var ex = JsonSerializer.Deserialize<ExclusionPayload>(e.PayloadJson, JsonOptions)!;
                next._exclusions[ex.MaterialId] =
                    new Exclusion(ex.MaterialId, ex.ReasonCode, ex.Detail, e.Actor, e.Id);
                break;

            case EventKind.ExclusionRevoked:
                if (next.Frozen) throw new BranchFrozenException(e.BranchId);
                var rx = JsonSerializer.Deserialize<ExclusionPayload>(e.PayloadJson, JsonOptions)!;
                next._exclusions.Remove(rx.MaterialId);
                break;

            case EventKind.FreezeRequested:
                var fz = JsonSerializer.Deserialize<FreezePayload>(e.PayloadJson, JsonOptions)!;
                next.Frozen = true;
                next.FrozenModelHash = fz.ModelHash;
                break;

            case EventKind.ManualDecision:
                // Decisions are recorded even on a frozen branch: they are review acts.
                var md = JsonSerializer.Deserialize<ManualDecisionPayload>(e.PayloadJson, JsonOptions)!;
                next._decisions.Add(md);
                if (md.Verdict == DecisionVerdict.Reject && md.TargetType == "exclusion" && !next.Frozen)
                    next._exclusions.Remove(md.TargetId);
                break;
        }
        return next;
    }

    public static ProjectState Fold(IEnumerable<ProjectEvent> events)
    {
        var state = new ProjectState();
        foreach (var e in events.OrderBy(x => x.Seq))
            state = state.Apply(e);
        return state;
    }

    private ProjectState Clone()
    {
        var c = new ProjectState
        {
            ProjectId = ProjectId,
            CoreLabel = CoreLabel,
            CoordinateBasis = CoordinateBasis,
            DepthDatumId = DepthDatumId,
            Frozen = Frozen,
            FrozenModelHash = FrozenModelHash,
            Parameters = Parameters,
        };
        foreach (var (k, v) in _current) c._current[k] = v;
        foreach (var (k, v) in _versions) c._versions[k] = new List<MaterialVersionRow>(v);
        c._events.AddRange(_events);
        foreach (var (k, v) in _exclusions) c._exclusions[k] = v;
        c._decisions.AddRange(_decisions);
        c._importDiagnostics.AddRange(_importDiagnostics);
        return c;
    }
}

public sealed class BranchFrozenException(string branchId)
    : InvalidOperationException($"branch '{branchId}' is frozen; mutate a child branch instead")
{
    public string BranchId { get; } = branchId;
}
