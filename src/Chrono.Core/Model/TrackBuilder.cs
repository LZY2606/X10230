using System;
using System.Collections.Generic;
using System.Linq;
using Chrono.Core.Materials;
using Chrono.Core.Quantities;

namespace Chrono.Core.Model;

/// <summary>
/// Assembles tracks from immutable materials: anchors are projected onto normalized depths,
    /// gaps/unconformities and faulted/overturned segments become explicit track boundaries.
    /// </summary>
    public sealed class TrackBuilder
    {
        public sealed class BuildResult
    {
        public List<Track> Tracks { get; } = new();
        public List<Diagnostic> Diagnostics { get; } = new();
        /// <summary>Depth -> anchor ids occupying the same normalized depth (duplicate anchors).</summary>
        public Dictionary<double, List<string>> DuplicateDepthAnchors { get; } = new();
    }

    public BuildResult Build(ProjectState state)
    {
        var result = new BuildResult();
        var core = state.CoreId;
        var segments = state.Materials.Values.OfType<CoreSegmentMaterial>()
            .Where(s => s.CoreId == core && !s.Superseded)
            .OrderBy(s => s.TopCm?.Cm ?? double.MaxValue).ToList();
        var gaps = state.Materials.Values.OfType<GapMaterial>()
            .Where(g => g.CoreId == core && !g.Superseded && g.AtCm is not null)
            .OrderBy(g => g.AtCm!.Value.Cm).ToList();

        var boundaryDepths = new SortedSet<double>();
        var boundaryAt = new Dictionary<double, List<string>>();
        foreach (var g in gaps)
        {
            boundaryDepths.Add(g.AtCm!.Value.Cm);
            AddBoundary(boundaryAt, g.AtCm!.Value.Cm, g.Id);
        }
        foreach (var seg in segments)
        {
            if (seg.Structure != SegmentStructure.Continuous)
            {
                // Faulted/overturned intervals are explicit boundaries: age cannot be interpolated
                // across them and monotonicity is NOT enforced across the interval.
                if (seg.TopCm is not null) { boundaryDepths.Add(seg.TopCm.Value.Cm); AddBoundary(boundaryAt, seg.TopCm.Value.Cm, seg.Id); }
                if (seg.BottomCm is not null) { boundaryDepths.Add(seg.BottomCm.Value.Cm); AddBoundary(boundaryAt, seg.BottomCm.Value.Cm, seg.Id); }
            }
        }

        // Continuous intervals between sorted boundaries.
        var intervals = new List<(Depth top, Depth bottom, List<string> bounds)>();
        double? start = segments.Count > 0 && segments[0].TopCm is not null ? segments[0].TopCm!.Value.Cm : null;
        double coreBottom = segments.Count > 0
            ? segments.Max(s => s.BottomCm?.Cm ?? double.MinValue) : 0;
        if (start is null) start = 0;
        foreach (var b in boundaryDepths)
        {
            if (b <= start.Value || b >= coreBottom) continue;
            intervals.Add((new Depth(start.Value), new Depth(b), boundaryAt.GetValueOrDefault(b, new List<string>())));
            start = b;
        }
        intervals.Add((new Depth(start.Value), new Depth(coreBottom), new List<string>()));

        var reservoirs = state.Materials.Values.OfType<ReservoirMaterial>()
            .Where(r => r.CoreId == core && !s_disabled(r, state) && r.Enabled)
            .ToDictionary(r => r.Id);

        var anchors = new List<Anchor>();
        foreach (var m in state.Materials.Values)
        {
            if (m.Superseded) continue;
            switch (m)
            {
                case RadiometricDateMaterial d when d.CoreId == core && d.AtCm is not null && !d.Excluded:
                {
                    IAgeDistribution age = d.MeasuredAge!;
                    if (d.ReservoirOffsetId is not null && reservoirs.TryGetValue(d.ReservoirOffsetId, out var off))
                        age = new ShiftedDistribution(age, off.OffsetDistribution);
                    anchors.Add(new Anchor(d.Id, d.AtCm.Value, age, "radiometric", d.Id));
                    break;
                }
                case EventTieMaterial e when e.CoreId == core && e.AtCm is not null && !e.Excluded
                    && CorrelationSelected(e, state):
                {
                    anchors.Add(new Anchor(e.Id, e.AtCm.Value, e.CorrelatedAge!, "event", e.Id)
                    {
                        CorrelationGroup = e.CorrelationGroup ?? ""
                    });
                    break;
                }
            }
        }

        // duplicate-depth detection across the whole core (they may land in the same track)
        foreach (var grp in anchors.GroupBy(a => a.Depth.Cm).Where(g => g.Count() > 1))
            result.DuplicateDepthAnchors[grp.Key] = grp.Select(a => a.Id).ToList();

        int ti = 0;
        foreach (var iv in intervals)
        {
            var inside = anchors.Where(a => a.Depth.Cm >= iv.top.Cm && a.Depth.Cm <= iv.bottom.Cm).ToList();
            var trackId = $"{core}.track[{ti}]";
            var bounds = iv.bounds.ToList();
            result.Tracks.Add(new Track(trackId, core, iv.top, iv.bottom, inside, bounds));
            ti++;
        }

        // surface materials with diagnostics / unconvertible values
        foreach (var m in state.Materials.Values.Where(m => m.Diagnostics.Count > 0))
            result.Diagnostics.AddRange(m.Diagnostics);
        return result;
    }

    private static bool s_disabled(ReservoirMaterial r, ProjectState state) => false;

    private static bool CorrelationSelected(EventTieMaterial e, ProjectState state)
    {
        if (string.IsNullOrEmpty(e.CorrelationGroup)) return true;
        return state.ChosenCorrelations.TryGetValue(e.CorrelationGroup, out var chosen) && chosen == e.Id;
    }

    private static void AddBoundary(Dictionary<double, List<string>> map, double depth, string id)
    {
        if (!map.TryGetValue(depth, out var list)) { list = new List<string>(); map[depth] = list; }
        list.Add(id);
    }
}
