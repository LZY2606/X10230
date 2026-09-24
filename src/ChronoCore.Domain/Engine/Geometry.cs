using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Engine;

/// <summary>Normalized geometry and validation verdicts independent of age
/// ordering. Every verdict carries a stable code and affected object ids,
/// forming structured state rather than free-text logs.</summary>
public sealed record GeometryModel(
    IReadOnlyList<NormSegment> Segments,
    IReadOnlyList<NormBoundary> Cuts,
    IReadOnlyList<NormAnchor> Anchors,
    IReadOnlyList<ContinuousSection> Sections,
    IReadOnlyList<Verdict> Verdicts);

public sealed record NormSegment(string Id, int Sequence, double Top, double Bottom, CoreSegment Raw);
public sealed record NormBoundary(string Id, BoundaryKind Kind, double Depth, double? MissingCm, Boundary Raw);
public sealed record NormAnchor(string Id, double Depth, double AgeCenter, AgeAnchor Raw, bool DepthOk);

public sealed record Verdict(
    string Code,
    DiagSeverity Severity,
    string Message,
    IReadOnlyList<string> AffectedIds,
    int? SourceLine);

public static class Geometry
{
    // 1 mm: depths at or closer than this on a shared datum are treated as ties.
    public const double TieEpsilonCm = 0.1;

    public static GeometryModel Build(CorePackage pkg)
    {
        var verdicts = new List<Verdict>();

        var segNorm = new List<NormSegment>();
        foreach (var s in pkg.Segments)
        {
            if (!UnitSystem.IsDepthConvertible(s.Top) || !UnitSystem.IsDepthConvertible(s.Bottom))
            {
                verdicts.Add(new("DEPTH_UNIT_UNCONVERTED", DiagSeverity.SemanticUnsolvable,
                    $"分段 {s.Id} 深度单位无法转换，几何位置不可判定（原值保留）", new[] { s.Id }, null));
                continue;
            }
            segNorm.Add(new NormSegment(s.Id, s.Sequence,
                UnitSystem.ToCm(s.Top), UnitSystem.ToCm(s.Bottom), s));
        }
        var ordered = segNorm.OrderBy(x => x.Top).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var cur = ordered[i];
            if (cur.Top >= cur.Bottom)
                verdicts.Add(new("SEGMENT_INVERTED", DiagSeverity.SemanticUnsolvable,
                    $"分段 {cur.Id} 顶深 {cur.Raw.Top.Raw} 不浅于底深 {cur.Raw.Bottom.Raw}（倒置层段）",
                    new[] { cur.Id }, null));
            if (i > 0)
            {
                var prev = ordered[i - 1];
                if (Math.Abs(cur.Top - prev.Top) <= TieEpsilonCm)
                    verdicts.Add(new("DEPTH_DUPLICATE", DiagSeverity.Warning,
                        $"深度重复：{prev.Id} 与 {cur.Id} 顶深同为 {cur.Raw.Top.Raw}",
                        new[] { prev.Id, cur.Id }, null));
            }
        }

        var cuts = new List<NormBoundary>();
        foreach (var b in pkg.Boundaries)
        {
            if (!UnitSystem.IsDepthConvertible(b.Depth))
            {
                verdicts.Add(new("BOUNDARY_UNCONVERTED", DiagSeverity.SemanticUnsolvable,
                    $"边界 {b.Id} 深度单位无法转换（原值保留）", new[] { b.Id }, null));
                continue;
            }
            var depth = UnitSystem.ToCm(b.Depth);
            var dup = cuts.FirstOrDefault(c => Math.Abs(c.Depth - depth) <= TieEpsilonCm);
            if (dup is not null)
            {
                verdicts.Add(new("BOUNDARY_DUPLICATE", DiagSeverity.Warning,
                    $"边界 {dup.Id} 与 {b.Id} 位于同一深度，合并为一个显式截断",
                    new[] { dup.Id, b.Id }, null));
                // keep the stronger cut kind
                if (b.Kind > dup.Kind) cuts[cuts.IndexOf(dup)] = dup with { Kind = b.Kind };
                continue;
            }
            cuts.Add(new NormBoundary(b.Id, b.Kind, depth, b.MissingIntervalDown, b));
        }
        cuts = cuts.OrderBy(c => c.Depth).ToList();

        var sections = CutSections(pkg, ordered, cuts);

        var anchorNorm = new List<NormAnchor>();
        foreach (var a in pkg.Anchors)
        {
            var ok = UnitSystem.IsDepthConvertible(a.Depth);
            var depth = ok ? UnitSystem.ToCm(a.Depth) : double.NaN;
            anchorNorm.Add(new NormAnchor(a.Id, depth, a.Age.Central, a, ok));
        }
        var anchors = anchorNorm.OrderBy(x => x.DepthOk ? x.Depth : double.PositiveInfinity)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

        for (var i = 1; i < anchors.Count; i++)
        {
            if (!anchors[i].DepthOk || !anchors[i - 1].DepthOk) continue;
            if (Math.Abs(anchors[i].Depth - anchors[i - 1].Depth) <= TieEpsilonCm)
                verdicts.Add(new("ANCHOR_TIED", DiagSeverity.Warning,
                    $"并列锚点：{anchors[i - 1].Id} 与 {anchors[i].Id} 位于同一深度 {anchors[i].Raw.Depth.Raw}",
                    new[] { anchors[i - 1].Id, anchors[i].Id }, anchors[i].Raw.SourceLine));
        }

        foreach (var a in anchors.Where(x => !x.DepthOk))
            verdicts.Add(new("ANCHOR_DEPTH_UNCONVERTED", DiagSeverity.SemanticUnsolvable,
                $"锚点 {a.Id} 深度单位无法转换，不能参与单调约束（原值保留）",
                new[] { a.Id }, a.Raw.SourceLine));

        foreach (var a in anchors.Where(x => x.DepthOk))
        {
            var cut = cuts.FirstOrDefault(c => c.MissingCm is not null &&
                a.Depth > c.Depth + TieEpsilonCm &&
                a.Depth < c.Depth + c.MissingCm!.Value - TieEpsilonCm);
            if (cut is not null)
                verdicts.Add(new("ANCHOR_IN_GAP", DiagSeverity.SemanticUnsolvable,
                    $"锚点 {a.Id} 落在边界 {cut.Id} 的缺失间隔内",
                    new[] { a.Id, cut.Id }, a.Raw.SourceLine));
        }

        return new GeometryModel(ordered, cuts, anchors, sections, verdicts);
    }

    private static IReadOnlyList<ContinuousSection> CutSections(
        CorePackage pkg, IReadOnlyList<NormSegment> ordered, IReadOnlyList<NormBoundary> cuts)
    {
        var datumId = pkg.Datum.Id;
        var sections = new List<ContinuousSection>();
        var order = 0;
        var current = new List<NormSegment>();
        double? top = null;

        void Emit(double bottom)
        {
            if (current.Count == 0) return;
            sections.Add(new ContinuousSection($"SEC-{order}", order,
                current.Select(s => s.Id).ToList(),
                new DepthRef(top!.Value, "cm", datumId, $"SEC-{order}.top"),
                new DepthRef(bottom, "cm", datumId, $"SEC-{order}.bottom")));
            order++;
            current.Clear();
            top = null;
        }

        var cutIdx = 0;
        foreach (var seg in ordered)
        {
            top ??= seg.Top;
            var segCuts = cuts.Where(c => c.Depth > seg.Top + TieEpsilonCm &&
                                          c.Depth < seg.Bottom - TieEpsilonCm).ToList();
            if (segCuts.Count == 0)
            {
                current.Add(seg);
                continue;
            }
            // segment straddles one or more explicit boundaries: split membership,
            // boundary itself is the section edge
            var cursor = seg.Top;
            foreach (var c in segCuts)
            {
                current.Add(new NormSegment(seg.Id, seg.Sequence, cursor, c.Depth, seg.Raw));
                Emit(c.Depth);
                cursor = c.Depth;
                cutIdx++;
            }
            current.Add(new NormSegment(seg.Id, seg.Sequence, cursor, seg.Bottom, seg.Raw));
            top ??= cursor;
        }
        if (current.Count > 0) Emit(current[^1].Bottom);
        _ = cutIdx;
        return sections;
    }
}
