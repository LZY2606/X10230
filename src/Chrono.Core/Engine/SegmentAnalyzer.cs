using Chrono.Core.Materials;
using Chrono.Core.Units;

namespace Chrono.Core.Engine;

public sealed record SegmentAnalysis(
    List<ChronologyChain> Chains,
    List<VariantGroup> VariantGroups,
    List<Anchor> ActiveAnchors);

public sealed record VariantGroup(
    string GroupId,
    string ChainId,
    string Kind,
    IReadOnlyList<string> AnchorIds);

internal sealed class RawChain
{
    public string RawId = "";
    public string Label = "";
    public int Order;
    public double Top;
    public double Bottom;
    public bool Unbroken = true;
    public List<string> Codes = new();
}

public sealed class SegmentAnalyzer
{
    private const double Eps = 1e-7;

    public SegmentAnalysis Analyze(MaterialProject project)
    {
        var excluded = new HashSet<string>(project.Exclusions.Select(e => e.TargetAnchorId));
        var anchors = project.Anchors.Where(a => !excluded.Contains(a.Id))
            .OrderBy(a => a.Depth.Meters).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        var barriers = project.Boundaries
            .Where(b => b.Kind is BoundaryKind.Unconformity or BoundaryKind.CoringGap)
            .OrderBy(b => b.Depth.Meters).ToList();

        var raw = new List<RawChain>();
        double cursor = double.NegativeInfinity;
        int order = 0;
        foreach (var barrier in barriers)
        {
            raw.Add(new RawChain { RawId = $"raw-{order}", Order = order, Top = cursor, Bottom = barrier.Depth.Meters });
            cursor = barrier.Depth.Meters;
            order++;
        }
        raw.Add(new RawChain { RawId = $"raw-{order}", Order = order, Top = cursor, Bottom = double.PositiveInfinity });

        // Named project segments map labels onto ranges when provided.
        foreach (var segment in project.Segments.OrderBy(s => s.Order))
        {
            var (lo, hi) = ResolveDepth(segment, project);
            var match = raw.FirstOrDefault(r => Math.Abs(r.Top - lo) < Eps &&
                (double.IsInfinity(hi) ? double.IsInfinity(r.Bottom) : Math.Abs(r.Bottom - hi) < Eps));
            if (match != null) match.Label = segment.Label;
        }

        // Explicit inversion markers split a range into a flagged overturned pair.
        foreach (var marker in project.Boundaries.Where(b => b.Kind == BoundaryKind.InversionMarker)
                     .OrderBy(b => b.Depth.Meters))
            SplitAt(raw, marker.Depth.Meters, "STRUCTURE-INVERSION-MARKED");

        var groups = new List<VariantGroup>();
        var chains = new List<ChronologyChain>();
        foreach (var r in raw)
        {
            var inChain = anchors.Where(a => a.Depth.Meters >= r.Top - Eps && a.Depth.Meters <= r.Bottom + Eps).ToList();
            var codes = new List<string>(r.Codes);
            DetectDuplicates(r, inChain, project, groups, codes);
            BuildTieGroups(r, inChain, groups);
            CheckMonotonic(inChain, codes);
            if (inChain.Count < 2) codes.Add("INFO-SINGLE-ANCHOR");
            chains.Add(new ChronologyChain(r.RawId, r.Label, r.Label, r.Order,
                r.Top, r.Bottom, r.Unbroken, inChain, codes.Distinct().ToList()));
        }

        chains = chains.Where(c => c.Anchors.Count > 0)
            .Select((c, i) => c with { ChainId = $"chain-{i + 1}", Order = i, SegmentId = c.Label })
            .ToList();

        var chainIdByRawTop = raw.Select((r, i) => (r.Top, i)).ToList();
        groups = groups.Select(g =>
        {
            var idx = raw.FindIndex(r => r.RawId == g.ChainId);
            if (idx < 0) return g;
            var depth = anchors.FirstOrDefault(a => g.AnchorIds.Contains(a.Id))?.Depth.Meters ?? 0;
            var target = chains.FirstOrDefault(c => c.InRange(depth));
            return target == null ? g : g with { ChainId = target.ChainId };
        }).Where(g => chains.Any(c => c.ChainId == g.ChainId)).ToList();

        return new SegmentAnalysis(chains, groups, anchors);
    }

    private static (double lo, double hi) ResolveDepth(Segment s, MaterialProject project)
    {
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        if (s.TopBoundaryId != null && project.Boundaries.FirstOrDefault(x => x.Id == s.TopBoundaryId) is { } tb)
            lo = tb.Depth.Meters;
        if (s.BottomBoundaryId != null && project.Boundaries.FirstOrDefault(x => x.Id == s.BottomBoundaryId) is { } bb)
            hi = bb.Depth.Meters;
        return (lo, hi);
    }

    private static void SplitAt(List<RawChain> raw, double depth, string code)
    {
        var target = raw.FirstOrDefault(r => depth > r.Top + Eps && depth < r.Bottom - Eps);
        if (target == null) return;
        int idx = raw.IndexOf(target);
        raw.RemoveAt(idx);
        raw.Insert(idx, new RawChain
        {
            RawId = target.RawId + "b", Order = target.Order, Top = depth, Bottom = target.Bottom,
            Unbroken = target.Unbroken, Codes = target.Codes.Append(code).ToList()
        });
        raw.Insert(idx, new RawChain
        {
            RawId = target.RawId + "a", Order = target.Order, Top = target.Top, Bottom = depth,
            Unbroken = target.Unbroken, Codes = target.Codes.Append(code).ToList()
        });
    }

    private static void DetectDuplicates(RawChain r, List<Anchor> inChain, MaterialProject project,
        List<VariantGroup> groups, List<string> codes)
    {
        var markedDepths = project.Boundaries.Where(b => b.Kind == BoundaryKind.DuplicateDepthMarker)
            .Select(b => b.Depth.Meters).ToHashSet();
        foreach (var grp in inChain.GroupBy(a => Math.Round(a.Depth.Meters, 6)).Where(g => g.Count() > 1))
        {
            bool marked = markedDepths.Contains(grp.Key);
            codes.Add(marked ? "STRUCTURE-DEPTH-DUPLICATE-MARKED" : "STRUCTURE-DEPTH-DUPLICATE-UNFLAGGED");
            string gid = grp.First().DuplicateGroupId ?? $"dup@{grp.Key:R}";
            groups.Add(new VariantGroup(gid, r.RawId,
                marked ? "DUPLICATE-MARKED" : "DUPLICATE-UNFLAGGED",
                grp.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => a.Id).ToList()));
        }
    }

    private static void BuildTieGroups(RawChain r, List<Anchor> inChain, List<VariantGroup> groups)
    {
        foreach (var tie in inChain.Where(a => a.TieGroupId != null).GroupBy(a => a.TieGroupId!))
        {
            groups.Add(new VariantGroup(tie.Key, r.RawId, "TIE-PARALLEL",
                tie.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => a.Id).ToList()));
        }
    }

    private static void CheckMonotonic(List<Anchor> ordered, List<string> codes)
    {
        for (int i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            if (Math.Abs(cur.Depth.Meters - prev.Depth.Meters) <= Eps) continue;
            double p = prev.MeasuredAge.Quantile(0.5);
            double c = cur.MeasuredAge.Quantile(0.5);
            if (!double.IsNaN(p) && !double.IsNaN(c) && c < p - 1e-9)
                codes.Add("SEMANTIC-INVERTED-AGE");
        }
    }
}
