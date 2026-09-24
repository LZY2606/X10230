using System.Text.Json;
using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;

namespace AgeDepthStudio.CasePackages;

public static class Replayer
{
    private static readonly JsonSerializerOptions O = new() { PropertyNameCaseInsensitive = true };

    public static List<StudioEvent> Replay(StudioService svc, CasePackage pkg)
    {
        var ws = pkg.WorkspaceId;
        svc.CreateWorkspace(ws, pkg.WorkspaceName, pkg.Kind);
        var emitted = new List<StudioEvent>();
        foreach (var st in pkg.Steps)
        {
            StudioEvent? evt = st.Op switch
            {
                "project" => svc.RegisterProject(ws, st.Data.Deserialize<CoreProject>(O)!, st.Actor),
                "segment" => svc.AddSegment(ws, st.Data.Deserialize<CoreSegment>(O)!, st.Actor),
                "gap" => svc.AddGap(ws, st.Data.Deserialize<SegmentGap>(O)!, st.Actor),
                "anchor" => svc.AddAnchor(ws, st.Data.Deserialize<AgeAnchor>(O)!, st.Actor),
                "offset" => svc.AddOffset(ws, st.Data.Deserialize<ReservoirOffset>(O)!, st.Actor),
                "layer" => svc.AddLayer(ws, st.Data.Deserialize<EventLayer>(O)!, st.Actor),
                "params" => EmitParams(svc, ws, st),
                "decision" => svc.RecordDecision(ws, st.Data.Deserialize<Decision>(O)!, st.Actor),
                "anchorRevise" => svc.ReviseAnchor(ws, st.Data.Deserialize<AgeAnchor>(O)!, st.Note, st.Actor),
                "recompute" => svc.Recompute(ws, st.ModelId, st.AffectedSegments, st.Id, st.Actor),
                "freeze" => svc.TryFreeze(ws, st.ModelId, st.ExpectedParamsId!, st.Actor, out var _)
                    ? svc.Store.Events(ws)[^1] : svc.Store.Events(ws)[^1],
                "trial" => RunTrial(svc, ws, st),
                "emit" => svc.Emit(ws, st.Data.GetProperty("type").GetString()!,
                    st.Data.GetProperty("payload"), st.Actor),
                _ => throw new InvalidDataException($"未知案例步骤 {st.Op}")
            };
            if (evt is not null) emitted.Add(evt);
        }
        return emitted;
    }

    private static StudioEvent EmitParams(StudioService svc, string ws, PackageStep st)
    {
        var rp = st.Data.Deserialize<RuleParams>(O)!;
        svc.IssueParams(ws, rp, true, st.Actor);
        return svc.Store.Events(ws)[^1];
    }

    private static StudioEvent RunTrial(StudioService svc, string ws, PackageStep st)
    {
        var o = st.Override!;
        var req = new TrialRequest(st.Id, st.DirectSourceId, st.Mutation,
            new TrialOverride(o.Kind, o.Id, o.DepthRaw, o.Samples, o.SegmentId, o.Budget));
        svc.RunTrial(ws, req, st.Adopt, st.Actor);
        return svc.Store.Events(ws)[^1];
    }
}
