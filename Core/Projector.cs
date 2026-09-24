namespace DepthAge.Core;

public sealed record DecisionView(long Seq, string Decision, string Target, string Reason, string By, string Branch);

public sealed record AnchorDisplay(
    string Ref,
    string MaterialId,
    string SourceKind,
    Quantity RawDepth,
    double? DepthM,
    AgeDistribution RawAge,
    double? Q05,
    double? Q50,
    double? Q95,
    bool Excluded,
    string? Note);

public sealed record BranchState(
    string Branch,
    IReadOnlyList<DecisionView> Decisions,
    IReadOnlyList<AnchorDisplay> Anchors,
    FitOutcome Fit);

public sealed record FrozenView(string ModelDigest, string Branch, string By, long Seq, ModelSet? Snapshot);

public sealed record ProjectedState(
    long UpToSeq,
    string Datum,
    EngineParams Params,
    IReadOnlyList<MaterialEnvelope> Materials,
    IReadOnlyList<ModelEvent> Events,
    IReadOnlyList<BranchState> Branches,
    IReadOnlyList<FrozenView> Freezes,
    IReadOnlyList<ExclusionDef> Exclusions,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// 投影器：从不可变材料 + 追加事件重建任意历史时点的可见结论。
/// 回到旧事件 = 用当时已存在的材料、参数版本与决定重新推导，绝不拿最新结果覆盖历史。
/// </summary>
public static class Projector
{
    public static ProjectedState Project(DepthAgeStore store, long? upToSeq = null)
    {
        var seqLimit = upToSeq ?? long.MaxValue;
        var events = store.Events(seqLimit);
        var materials = store.Materials(seqLimit);
        var diags = new List<Diagnostic>();

        var parameters = new EngineParams();
        var decisions = new List<DecisionView>();
        foreach (var e in events)
        {
            if (e.Kind == ModelEvent.ParamRevisionKind)
            {
                var pr = JsonOptions.Deserialize<ParamRevisionPayload>(e.PayloadJson);
                if (pr is not null)
                    parameters = parameters with { ParamVersion = pr.ParamVersion, DefaultReservoirOffsetKa = pr.DefaultReservoirOffsetKa };
            }
            else if (e.Kind == ModelEvent.DecisionKind)
            {
                var d = JsonOptions.Deserialize<DecisionPayload>(e.PayloadJson);
                if (d is not null)
                    decisions.Add(new DecisionView(e.Seq, d.Decision, d.Target, d.Reason, d.By, d.Branch));
            }
        }

        var datum = "unspecified";
        var segments = new List<SegmentInput>();
        var anchors = new List<(string Ref, string MaterialId, string SourceKind, AgeAnchorDef? Anchor, EventLayerDef? Layer)>();
        var exclusions = new List<ExclusionDef>();

        foreach (var m in materials)
        {
            switch (m.Kind)
            {
                case MaterialEnvelope.DepthDatumKind:
                    datum = JsonOptions.Deserialize<DepthDatumDef>(m.RawJson)?.Name ?? datum;
                    break;
                case MaterialEnvelope.SegmentKind:
                {
                    var def = JsonOptions.Deserialize<CoreSegmentDef>(m.RawJson);
                    if (def is null)
                    {
                        diags.Add(new Diagnostic("material-corrupt", Diagnostic.Error,
                            $"segment material {m.Id} cannot be parsed; kept in store, excluded from fit", null, $"material:{m.Id}"));
                        break;
                    }
                    segments.Add(new SegmentInput(def.Segment, def.Top, def.Bottom, def.TopBoundary, def.BottomBoundary));
                    break;
                }
                case MaterialEnvelope.AnchorKind:
                {
                    var def = JsonOptions.Deserialize<AgeAnchorDef>(m.RawJson);
                    if (def is null)
                    {
                        diags.Add(new Diagnostic("material-corrupt", Diagnostic.Error,
                            $"anchor material {m.Id} cannot be parsed; kept in store, excluded from fit", null, $"material:{m.Id}"));
                        break;
                    }
                    anchors.Add((def.Anchor, m.Id, "age-anchor", def, null));
                    break;
                }
                case MaterialEnvelope.EventLayerKind:
                {
                    var def = JsonOptions.Deserialize<EventLayerDef>(m.RawJson);
                    if (def is null || def.EventAge is null)
                    {
                        diags.Add(new Diagnostic("material-corrupt", Diagnostic.Error,
                            $"event layer {m.Id} cannot be parsed or lacks an event age; kept in store", null, $"material:{m.Id}"));
                        break;
                    }
                    anchors.Add((def.Layer, m.Id, "event-layer",
                        new AgeAnchorDef(def.Layer, def.Depth, def.EventAge, null, def.CorrelatesTo, "event-correlation", null), def));
                    break;
                }
                case MaterialEnvelope.ExclusionKind:
                {
                    var def = JsonOptions.Deserialize<ExclusionDef>(m.RawJson);
                    if (def is not null) exclusions.Add(def);
                    break;
                }
            }
        }

        foreach (var ex in exclusions)
            diags.Add(new Diagnostic("exclusion-kept", Diagnostic.Info,
                $"exclusion decision preserved: {ex.Target} excluded by {ex.DecidedBy} — {ex.Reason}",
                ex.Target, $"exclusion:{ex.Target}"));

        var branchNames = decisions.Select(d => d.Branch)
            .Where(b => b != DecisionPayload.MainBranch)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();
        branchNames.Insert(0, DecisionPayload.MainBranch);

        var branches = new List<BranchState>();
        foreach (var branch in branchNames)
        {
            var bd = decisions.Where(d => d.Branch == branch).ToList();
            var rejected = bd.Where(d => d.Decision == DecisionPayload.Reject).Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
            foreach (var a in bd.Where(d => d.Decision == DecisionPayload.Accept)) rejected.Remove(a.Target);
            var excludedRefs = exclusions.Select(x => x.Target).Concat(rejected).ToHashSet(StringComparer.Ordinal);

            // 取代关系：新材料 Supersedes 旧锚点；若取代者在本分支被否决，旧锚点复活。
            var active = anchors.Where(a => !excludedRefs.Contains(a.Ref)).ToList();
            var superseded = active
                .Where(a => a.Anchor?.Supersedes is not null)
                .Select(a => a.Anchor!.Supersedes!)
                .ToHashSet(StringComparer.Ordinal);
            active = active.Where(a => !superseded.Contains(a.Ref)).ToList();

            var inputs = active.Select(a => new AnchorInput(a.Ref, a.MaterialId, a.SourceKind,
                a.Anchor!.Depth, a.Anchor.Age, a.Anchor.ReservoirOffset)).ToList();
            var fit = Engine.Fit(segments, inputs, parameters);

            var displays = anchors.Select(a =>
            {
                var excluded = excludedRefs.Contains(a.Ref) || superseded.Contains(a.Ref);
                var note = excludedRefs.Contains(a.Ref) ? "excluded by decision"
                    : superseded.Contains(a.Ref) ? $"superseded by {active.FirstOrDefault(x => x.Anchor?.Supersedes == a.Ref).Ref ?? "newer material"}"
                    : null;
                double? depthM = null, q05 = null, q50 = null, q95 = null;
                if (UnitRegistry.TryConvert(a.Anchor!.Depth, QuantityKind.Depth, out var dm, out _))
                {
                    depthM = dm.Value;
                    var offset = a.Anchor.ReservoirOffset is { } off &&
                                 UnitRegistry.TryConvert(off, QuantityKind.Offset, out var oc, out _)
                        ? oc.Value
                        : parameters.DefaultReservoirOffsetKa;
                    var factor = a.Anchor.Age.Unit.Trim().ToLowerInvariant() switch
                    {
                        "ka" or "kyr" => 1.0,
                        "yr" or "a" or "year" or "years" => 0.001,
                        "ma" or "myr" => 1000.0,
                        _ => double.NaN
                    };
                    if (!double.IsNaN(factor))
                    {
                        var d = a.Anchor.Age.WithLinearTransform(factor, -offset, UnitRegistry.CanonicalAgeUnit);
                        q05 = d.Quantile(0.05); q50 = d.Quantile(0.50); q95 = d.Quantile(0.95);
                    }
                }
                return new AnchorDisplay(a.Ref, a.MaterialId, a.SourceKind, a.Anchor.Depth, depthM,
                    a.Anchor.Age, q05, q50, q95, excluded, note);
            }).ToList();

            branches.Add(new BranchState(branch, bd, displays, fit));
        }

        var freezes = store.Freezes(seqLimit).Select(f =>
        {
            ModelSet? snapshot = null;
            var fe = events.LastOrDefault(e => e.Kind == ModelEvent.FreezeKind && e.Seq == f.Seq);
            if (fe is not null)
                snapshot = JsonOptions.Deserialize<FreezePayload>(fe.PayloadJson)?.Snapshot;
            return new FrozenView(f.ModelId, f.Branch, f.By, f.Seq, snapshot);
        }).ToList();

        return new ProjectedState(seqLimit == long.MaxValue ? store.MaxSeq() : seqLimit,
            datum, parameters, materials, events, branches, freezes, exclusions, diags);
    }
}
