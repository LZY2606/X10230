namespace AgeDepth.Core;

public sealed record Segment(
    string Id, double TopM, double BottomM, bool Inverted,
    string[] BoundaryIds, string[] SectionIds)
{
    public string Datum = "mbsf";
}

public sealed record SegmentationResult(List<Segment> Segments, List<Diagnostic> Diagnostics);

public static class Segmenter
{
    public const double DepthEpsilon = 1e-9;

    /// <summary>
    /// 由岩芯分段 + 显式边界切出不断层段。
    /// 深度重复、倒置层段、缺口都形成结构化诊断，不静默修正。
    /// </summary>
    public static SegmentationResult Build(IReadOnlyList<CoreSection> sections, IReadOnlyList<Boundary> boundaries)
    {
        var diags = new List<Diagnostic>();
        var usable = new List<(CoreSection s, double top, double bottom, bool inverted)>();

        foreach (var s in sections)
        {
            if (s.Top.Canonical is null || s.Bottom.Canonical is null)
            {
                diags.Add(new Diagnostic("SECTION_DEPTH_UNCONVERTIBLE", $"section:{s.Id}",
                    $"分段 '{s.Label}' 深度无法规范化，保留原始表示并排除出数值管线"));
                continue;
            }
            var (top, bottom) = (s.Top.Canonical.Value, s.Bottom.Canonical.Value);
            var inverted = top > bottom;
            if (inverted)
                diags.Add(new Diagnostic("SECTION_INVERTED", $"section:{s.Id}",
                    $"倒置层段 '{s.Label}': 顶 {top:0.###} m > 底 {bottom:0.###} m，保留但不参与单调建模"));
            usable.Add((s, Math.Min(top, bottom), Math.Max(top, bottom), inverted));
        }

        // 切点：分段边缘 + 显式边界（含 gap 区间两端）
        var cuts = new SortedDictionary<double, List<string>>();
        void AddCut(double depth, string source)
        {
            var key = cuts.Keys.FirstOrDefault(k => Math.Abs(k - depth) <= DepthEpsilon);
            if (cuts.Keys.Any(k => Math.Abs(k - depth) <= DepthEpsilon))
            {
                key = cuts.Keys.First(k => Math.Abs(k - depth) <= DepthEpsilon);
                if (!cuts[key].Contains(source))
                {
                    cuts[key].Add(source);
                    diags.Add(new Diagnostic("DEPTH_DUPLICATE", $"boundary:{source}",
                        $"深度 {depth:0.###} m 处切点重复: {string.Join(", ", cuts[key])}；两者均保留，按稳定顺序处理"));
                }
            }
            else cuts[depth] = new List<string> { source };
        }

        foreach (var (s, top, bottom, _) in usable)
        {
            AddCut(top, $"section:{s.Id}:top");
            AddCut(bottom, $"section:{s.Id}:bottom");
        }
        var boundaryIdsAt = new Dictionary<double, List<string>>();
        foreach (var b in boundaries)
        {
            if (b.Depth.Canonical is null)
            {
                diags.Add(new Diagnostic("BOUNDARY_DEPTH_UNCONVERTIBLE", $"boundary:{b.Id}",
                    $"边界 {b.Id} ({b.Kind}) 深度无法规范化，保留原始表示"));
                continue;
            }
            AddCut(b.Depth.Canonical.Value, $"boundary:{b.Id}");
            var key = cuts.Keys.First(k => Math.Abs(k - b.Depth.Canonical.Value) <= DepthEpsilon);
            (boundaryIdsAt.TryGetValue(key, out var l) ? l : boundaryIdsAt[key] = new List<string>()).Add(b.Id);
            if (b.DepthTo?.Canonical is { } to)
            {
                AddCut(to, $"boundary:{b.Id}:to");
                var k2 = cuts.Keys.First(k => Math.Abs(k - to) <= DepthEpsilon);
                (boundaryIdsAt.TryGetValue(k2, out var l2) ? l2 : boundaryIdsAt[k2] = new List<string>()).Add(b.Id);
            }
        }

        // 相邻切点间被分段覆盖的区间 → 不断层段；未被覆盖的区间 → 隐式缺口诊断
        var segments = new List<Segment>();
        var keys = cuts.Keys.ToArray();
        for (var i = 0; i < keys.Length - 1; i++)
        {
            var (top, bottom) = (keys[i], keys[i + 1]);
            var covering = usable.Where(u => u.top <= top + DepthEpsilon && u.bottom >= bottom - DepthEpsilon).ToList();
            if (covering.Count == 0)
            {
                diags.Add(new Diagnostic("GAP_IMPLICIT", $"gap:{top:0.###}-{bottom:0.###}",
                    $"分段缺口 {top:0.###}–{bottom:0.###} m：无岩芯覆盖，作为显式边界处理"));
                continue;
            }
            var inverted = covering.Any(c => c.inverted);
            var bIds = new List<string>();
            if (boundaryIdsAt.TryGetValue(top, out var bt)) bIds.AddRange(bt);
            if (boundaryIdsAt.TryGetValue(bottom, out var bb)) bIds.AddRange(bb);
            segments.Add(new Segment(
                $"seg-{top:0.###}-{bottom:0.###}", top, bottom, inverted,
                bIds.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                covering.Select(c => c.s.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        }

        // 稳定顺序：先按顶深，再按 id
        segments = segments.OrderBy(s => s.TopM).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        return new SegmentationResult(segments, diags);
    }
}
