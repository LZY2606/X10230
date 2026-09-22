using System.Text.Json;
using Chrono.Core.Case;
using Chrono.Core.Model;

namespace Chrono.Core.Sourcing;

/// <summary>
/// Folds the append-only event log into a <see cref="ProjectState"/>. Because the log is immutable,
/// folding up to any historical event restores that exact dependency/parameter version.
/// </summary>
public static class Projector
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ProjectState Empty(string projectId, string name)
        => new() { ProjectId = projectId, Name = name };

    public static ProjectState Fold(IEnumerable<ChronoEvent> events)
    {
        var list = events.OrderBy(e => e.Seq).ToList();
        if (list.Count == 0) throw new ArgumentException("no events", nameof(events));
        var pc = Deserialize<ProjectCreatedPayload>(list[0]);
        var state = Empty(list[0].ProjectId, pc?.Name ?? list[0].ProjectId);
        foreach (var ev in list) Apply(state, ev);
        return state;
    }

    public static ProjectState FoldUntil(IEnumerable<ChronoEvent> events, string untilEventId)
    {
        var ordered = events.OrderBy(e => e.Seq).ToList();
        var cut = ordered.FindIndex(e => e.Id == untilEventId);
        if (cut < 0) throw new KeyNotFoundException(untilEventId);
        return Fold(ordered.Take(cut + 1));
    }

    public static void Apply(ProjectState state, ChronoEvent ev)
    {
        state.AppliedEvents.Add(ev);
        switch (ev.Type)
        {
            case ChronoEvent.ProjectCreated:
            {
                var p = Deserialize<ProjectCreatedPayload>(ev);
                state.Name = p?.Name ?? state.Name;
                state.CaseId = p?.CaseId ?? "";
                break;
            }
            case ChronoEvent.BaselineIngested:
                ApplyBaseline(state, ev);
                break;
            case ChronoEvent.MaterialAmended:
                ApplyAmend(state, ev);
                break;
            case ChronoEvent.ExclusionDecided:
            {
                var p = Deserialize<ExclusionPayload>(ev)!;
                state.Exclusions[p.AnchorId] = new Exclusion(
                    "ex_" + p.AnchorId, p.AnchorId, p.Reason, p.DecidedBy, ev.Id, p.Active, state.MaterialsVersion);
                break;
            }
            case ChronoEvent.DuplicateResolved:
            {
                var p = Deserialize<DuplicateResolvePayload>(ev)!;
                state.DuplicateResolutions.Add(new DuplicateResolution(
                    p.DepthMetres, p.AnchorIds, p.Mode, p.DroppedAnchorId, ev.Id));
                break;
            }
            case ChronoEvent.InversionBoundaryAdded:
            {
                var p = Deserialize<InversionBoundaryPayload>(ev)!;
                var contact = p.Contact == "recovery-gap" ? ContactKind.RecoveryGap : ContactKind.Unconformity;
                state.AddedBoundaries.Add(new AddedBoundary(p.AtDepthMetres, contact, p.Justification, ev.Id));
                state.MaterialsVersion++;
                break;
            }
            case ChronoEvent.ParametersRevised:
            {
                var p = Deserialize<ParametersPayload>(ev)!;
                state.Parameters = new DeriveParameters(
                    state.Parameters.Version + 1,
                    p.Samples ?? state.Parameters.Samples,
                    p.DepthGridPoints ?? state.Parameters.DepthGridPoints,
                    p.BudgetDraws ?? state.Parameters.BudgetDraws,
                    p.Strategies ?? state.Parameters.Strategies,
                    p.ApplyReservoir ?? state.Parameters.ApplyReservoir);
                break;
            }
            case ChronoEvent.CandidateDecided:
            {
                var p = Deserialize<CandidateDecisionPayload>(ev)!;
                state.CandidateDecisions.Add(new CandidateDecision(
                    p.RunId, p.AcceptedCandidateIds, p.Decision, ev.Id, ev.Seq));
                break;
            }
            case ChronoEvent.ModelFrozen:
            {
                var p = Deserialize<FreezePayload>(ev)!;
                state.Frozen.Add(new FrozenModel(p.RunId, p.CandidateIds, state.Parameters.Version,
                    p.CompareToken, ev.Id, ev.Seq));
                break;
            }
            case ChronoEvent.BranchForked:
                state.ForkedFrom = ev.ForkFromEventId;
                break;
            case ChronoEvent.CompareAnchorAdded:
            {
                var p = Deserialize<CompareAnchorPayload>(ev)!;
                var (dist, reason) = FieldCodec.ParseAge(p.RawAge);
                state.CompareAnchors.Add(new Anchor(
                    "cmp_" + StableId.Sha1("a", $"{p.DepthMetres:R}|{p.RawAge}")[..18],
                    p.Label, p.DepthMetres.ToString("R"), "m", p.DepthMetres,
                    p.RawAge, dist, null, "hypothetical", false, null, state.MaterialsVersion, reason));
                break;
            }
            case ChronoEvent.RuleFinding:
            {
                var p = Deserialize<FindingPayload>(ev)!;
                state.Findings.Add(new RuleFinding(p.Code, p.Subject, p.Holds, p.Justification,
                    p.AffectedObjectIds, ev.Id, ev.Seq));
                break;
            }
        }
    }

    private static void ApplyBaseline(ProjectState state, ChronoEvent ev)
    {
        var p = Deserialize<BaselineIngestPayload>(ev)!;
        state.CaseId = p.CaseId;
        state.CaseVersion = p.CaseVersion;
        state.Datum = new DepthDatum(p.DatumId, p.DatumRaw, p.DatumFrame, p.DatumTopMetres);
        state.MaterialsVersion = 1;

        foreach (var s in p.Segments)
            state.Segments[s.Id] = new Segment(s.Id, s.Code, s.RawTop, s.RawTopUnit, s.RawBottom,
                s.RawBottomUnit, s.TopMetres, s.BottomMetres, ParseContact(s.LowerContact),
                s.ContactRaw, s.Lithology, 1);
        foreach (var a in p.Anchors)
        {
            var (dist, _) = FieldCodec.ParseAge(a.RawAge);
            state.Anchors[a.Id] = new Anchor(a.Id, a.Label, a.RawDepth, a.RawDepthUnit, a.DepthMetres,
                a.RawAge, dist, a.ReservoirOffsetId, a.Material, false, null, 1, a.UnconvertibleReason);
        }
        foreach (var e in p.Events)
            state.Events[e.Id] = new EventLayer(e.Id, e.Code, e.RawDepth, e.RawDepthUnit,
                e.DepthMetres, e.Correlation, e.ExternalAgeReference, 1);
        foreach (var r in p.Reservoirs)
            state.Reservoirs[r.Id] = new ReservoirOffset(r.Id, r.MeanKa, r.SigmaKa, r.ReferenceCurve, r.RawText);
        foreach (var x in p.Exclusions)
            state.Exclusions[x.AnchorId] = new Exclusion(x.Id, x.AnchorId, x.Reason, x.DecidedBy, ev.Id, true, 1);
    }

    private static void ApplyAmend(ProjectState state, ChronoEvent ev)
    {
        var p = Deserialize<MaterialAmendPayload>(ev)!;
        state.MaterialsVersion++;
        int v = state.MaterialsVersion;
        switch (p.Kind)
        {
            case "anchor" when state.Anchors.TryGetValue(p.MaterialId, out var a):
                state.Anchors[a.Id] = AmendAnchor(a, p.Fields, v);
                break;
            case "segment" when state.Segments.TryGetValue(p.MaterialId, out var s):
                state.Segments[s.Id] = AmendSegment(s, p.Fields, v);
                break;
            case "event" when state.Events.TryGetValue(p.MaterialId, out var e):
                state.Events[e.Id] = AmendEvent(e, p.Fields, v);
                break;
            case "reservoir" when state.Reservoirs.TryGetValue(p.MaterialId, out var r):
                state.Reservoirs[r.Id] = AmendReservoir(r, p.Fields);
                break;
        }
    }

    private static Anchor AmendAnchor(Anchor a, Dictionary<string, string?> f, int v)
    {
        var result = a with { SourceVersion = v };
        if (f.TryGetValue("label", out var label) && label is not null) result = result with { Label = label };
        if (f.TryGetValue("material", out var mat) && mat is not null) result = result with { Material = mat };
        if (f.TryGetValue("reservoirId", out var rid)) result = result with { ReservoirOffsetId = string.IsNullOrEmpty(rid) ? null : rid };
        if (f.TryGetValue("rawAge", out var age) && age is not null)
        {
            var (dist, reason) = FieldCodec.ParseAge(age);
            result = result with { RawAge = age, Distribution = dist, UnconvertibleReason = reason };
        }
        if (f.TryGetValue("rawDepth", out var rd) && rd is not null)
        {
            var unit = f.TryGetValue("rawDepthUnit", out var u) && u is not null ? u : result.RawDepthUnit;
            var (m, reason) = FieldCodec.ParseDepth(rd, unit);
            result = result with { RawDepth = rd, RawDepthUnit = unit, DepthMetres = m, UnconvertibleReason = reason };
        }
        return result;
    }

    private static Segment AmendSegment(Segment s, Dictionary<string, string?> f, int v)
    {
        var r = s with { SourceVersion = v };
        if (f.TryGetValue("top", out var top) && top is not null)
        {
            var unit = f.TryGetValue("topUnit", out var u) && u is not null ? u : r.RawTopUnit;
            var (m, reason) = FieldCodec.ParseDepth(top, unit);
            r = r with { RawTop = top, RawTopUnit = unit, TopMetres = m ?? r.TopMetres };
        }
        if (f.TryGetValue("bottom", out var bot) && bot is not null)
        {
            var unit = f.TryGetValue("bottomUnit", out var u) && u is not null ? u : r.RawBottomUnit;
            var (m, reason) = FieldCodec.ParseDepth(bot, unit);
            r = r with { RawBottom = bot, RawBottomUnit = unit, BottomMetres = m ?? r.BottomMetres };
        }
        if (f.TryGetValue("lowerContact", out var c) && c is not null)
            r = r with { LowerContact = ParseContact(c), ContactRaw = c };
        if (f.TryGetValue("lithology", out var lith) && lith is not null)
            r = r with { Lithology = lith };
        return r;
    }

    private static EventLayer AmendEvent(EventLayer e, Dictionary<string, string?> f, int v)
    {
        var r = e with { SourceVersion = v };
        if (f.TryGetValue("correlation", out var c) && c is not null) r = r with { Correlation = c };
        if (f.TryGetValue("externalAgeReference", out var x) && x is not null) r = r with { ExternalAgeReference = x };
        if (f.TryGetValue("rawDepth", out var rd) && rd is not null)
        {
            var unit = f.TryGetValue("rawDepthUnit", out var u) && u is not null ? u : r.RawDepthUnit;
            var (m, _) = FieldCodec.ParseDepth(rd, unit);
            r = r with { RawDepth = rd, RawDepthUnit = unit, DepthMetres = m };
        }
        return r;
    }

    private static ReservoirOffset AmendReservoir(ReservoirOffset r, Dictionary<string, string?> f)
    {
        double mean = r.MeanKa, sig = r.SigmaKa;
        if (f.TryGetValue("meanKa", out var mn) && double.TryParse(mn, out var mv)) mean = mv;
        if (f.TryGetValue("sigmaKa", out var sg) && double.TryParse(sg, out var sv)) sig = sv;
        return r with { MeanKa = mean, SigmaKa = sig };
    }

    public static ContactKind ParseContact(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "unconformity" or "hiatus" or "gap" => ContactKind.Unconformity,
        "recovery-gap" or "recovery" => ContactKind.RecoveryGap,
        _ => ContactKind.Conformable,
    };

    public static T? Deserialize<T>(ChronoEvent ev) => JsonSerializer.Deserialize<T>(ev.PayloadJson, JsonOpts);
}
