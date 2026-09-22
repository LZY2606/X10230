using System.Text.Json;

namespace GeoChron.Core;

/// <summary>
/// 由分段与显式缺口构建“连续层段”。年龄只能在不断层段内单调；
/// 深度缺口与不整合是显式边界，插值永远不跨越它们。
/// </summary>
public static class SectionBuilder
{
    public sealed record BuiltSections(IReadOnlyList<Section> Sections, IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<Finding> Findings, double TopCm, double BottomCm);

    public static BuiltSections Build(CasePack pack)
    {
        var diags = new List<Diagnostic>();
        var findings = new List<Finding>();
        var segs = pack.Segments;
        var segVals = new List<(Segment seg, double top, double bottom)>();
        foreach (var s in segs)
        {
            if (!s.TopDepth.IsConvertible || !s.BottomDepth.IsConvertible)
            {
                diags.Add(new Diagnostic("S001", $"segments[{s.Id}]", "分段端点存在不可转换深度，保留原值，不参与层段构建"));
                continue;
            }
            var top = s.TopDepth.Canonical!.Value;
            var bottom = s.BottomDepth.Canonical!.Value;
            if (bottom < top)
                findings.Add(new Finding(FindingKind.SegmentInverted, new[] { s.Id }, null, top,
                    $"分段 {s.Label} 顶底倒置（top {s.TopDepth.Raw} 深于 bottom {s.BottomDepth.Raw}），该分段判为无效"));
            else
                segVals.Add((s, top, bottom));
        }
        var ordered = segVals.OrderBy(x => x.top).ThenBy(x => x.bottom).ThenBy(x => x.seg.Id, StringComparer.Ordinal).ToList();

        var gaps = new List<(Gap gap, double depth)>();
        foreach (var g in pack.Gaps)
        {
            if (!g.AtOrBelowDepth.IsConvertible)
            {
                diags.Add(new Diagnostic("S002", $"gaps[{g.Id}]", "缺口深度不可转换，保留原值"));
                continue;
            }
            gaps.Add((g, g.AtOrBelowDepth.Canonical!.Value));
        }
        gaps.Sort((a, b) => a.depth.CompareTo(b.depth));

        var sections = new List<Section>();
        var current = new List<(Segment, double, double)>();
        var idx = 0;
        double? topCm = ordered.Count > 0 ? ordered[0].top : null;
        double bottomCm = ordered.Count > 0 ? ordered[^1].bottom : 0;

        void Close(double? lowerGapDepth)
        {
            if (current.Count == 0) return;
            var gapId = gaps.FirstOrDefault(g => g.depth == lowerGapDepth).gap?.Id;
            sections.Add(new Section(
                $"sec-{idx + 1}", idx, current[0].Item2, current[^1].Item3,
                current.Select(c => c.Item1.Id).ToList(), gapId));
            idx++;
            current.Clear();
        }

        var gapDepths = gaps.Select(g => g.depth).ToHashSet();
        for (var i = 0; i < ordered.Count; i++)
        {
            current.Add(ordered[i]);
            var nextTop = i + 1 < ordered.Count ? ordered[i + 1].top : (double?)null;
            var curBottom = ordered[i].bottom;
            if (nextTop is null) { Close(null); break; }
            var gap = gaps.FirstOrDefault(g => g.depth > curBottom - 1e-9 && g.depth < nextTop.Value + 1e-9);
            if (gap.gap is not null)
            {
                findings.Add(new Finding(FindingKind.GapBoundary, new[] { gap.gap.Id }, null, gap.depth,
                    $"{gap.gap.Kind} 边界 {gap.gap.Label}（{gap.gap.Justification}）：插值禁止跨越"));
                Close(gap.depth);
            }
        }
        if (sections.Count == 0 && ordered.Count == 0)
            diags.Add(new Diagnostic("S003", "segments", "没有任何可用于建模的有效分段"));
        return new BuiltSections(sections, diags, findings, topCm ?? 0, bottomCm);
    }

    /// <summary>找到深度所在层段（不落在任何层段内则返回 null）。</summary>
    public static Section? SectionAt(this IReadOnlyList<Section> sections, double depthCm) =>
        sections.FirstOrDefault(s => depthCm >= s.TopCm - 1e-9 && depthCm <= s.BottomCm + 1e-9);
}

/// <summary>PAVA 保序（单调非减，深度增大→年龄变老）回归，返回每个块（起索引、止索引、块年龄）。</summary>
public static class Isotonic
{
    public static List<(int start, int end, double value)> NonDecreasing(double[] ages, double[] weights)
    {
        var blocks = new List<(int start, int end, double sumW, double sumX)>();
        for (var i = 0; i < ages.Length; i++)
        {
            blocks.Add((i, i, weights[i], ages[i] * weights[i]));
            while (blocks.Count > 1)
            {
                var a = blocks[^2];
                var b = blocks[^1];
                var va = a.sumX / a.sumW;
                var vb = b.sumX / b.sumW;
                if (va <= vb + 1e-12) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (a.start, b.end, a.sumW + b.sumW, a.sumX + b.sumX);
            }
        }
        return blocks.Select(bl => (bl.start, bl.end, bl.sumX / bl.sumW)).ToList();
    }
}
