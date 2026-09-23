using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

/// <summary>
/// 不确定度带：在锚点之间按样本联合抽样插值；带只在不断层段内有效，
/// 缺口/不整合/倒置边界处断开，绝不跨边界绘制。
/// </summary>
public static class BandFactory
{
    public static List<UncertaintyBand> ForSegment(ModelSegment segment, List<PreparedAnchor> ordered, bool pava)
    {
        if (ordered.Count == 0)
            return new List<UncertaintyBand> { EmptyBand(segment) };
        if (ordered.Count == 1)
            return new List<UncertaintyBand> { SinglePointBand(segment, ordered[0]) };

        var points = new List<BandPoint>();
        var n = Math.Min(400, ordered.Min(p => p.Samples.Count));

        void Add(double depthM, List<PreparedAnchor> a, List<PreparedAnchor> b)
        {
            var lo = new List<double>(n);
            var hi = new List<double>(n);
            var med = new List<double>(n);
            for (var k = 0; k < n; k++)
            {
                var ages = new List<double>(a.Count + b.Count);
                foreach (var p in a) ages.Add(p.Samples[k % p.Samples.Count]);
                foreach (var p in b) ages.Add(p.Samples[k % p.Samples.Count]);
                ages.Sort();
                med.Add(ages[ages.Count / 2]);
                lo.Add(ages[(int)(0.025 * (ages.Count - 1))]);
                hi.Add(ages[(int)(0.975 * (ages.Count - 1))]);
            }
            lo.Sort(); hi.Sort(); med.Sort();
            points.Add(new BandPoint(depthM, Percentile(lo, 0.025), Percentile(med, 0.5), Percentile(hi, 0.975)));
        }

        if (!pava)
        {
            Add(ordered[0].DepthM, new(), new() { ordered[0] });
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var mid = (ordered[i].DepthM + ordered[i + 1].DepthM) / 2.0;
                Add(mid, new() { ordered[i] }, new() { ordered[i + 1] });
                Add(ordered[i + 1].DepthM, new(), new() { ordered[i + 1] });
            }
        }
        else
        {
            // 直接在原始锚点深度上打点；PAVA 合并块使用块内混合样本（合并采样）
            var pooling = PoolMap(ordered);
            var representatives = ordered
                .Select(p => pooling.TryGetValue(p.Anchor.Id, out var group) ? MergeGroup(group, p.DepthM) : p)
                .GroupBy(r => r.DepthM)
                .Select(g => g.First())
                .ToList();
            Add(representatives[0].DepthM, new(), new() { representatives[0] });
            for (var i = 0; i < representatives.Count - 1; i++)
            {
                var mid = (representatives[i].DepthM + representatives[i + 1].DepthM) / 2.0;
                Add(mid, new() { representatives[i] }, new() { representatives[i + 1] });
                Add(representatives[i + 1].DepthM, new(), new() { representatives[i + 1] });
            }
        }

        return new List<UncertaintyBand>
        {
            new()
            {
                SegmentId = segment.Id,
                Points = points.OrderBy(p => p.DepthM).ToList(),
                EffectiveTopDepthM = segment.TopDepthM,
                EffectiveBottomDepthM = segment.BottomDepthM
            }
        };
    }

    private static PreparedAnchor MergeGroup(List<PreparedAnchor> group, double depthM)
    {
        var n = Math.Min(400, group.Min(p => p.Samples.Count));
        var merged = new List<double>(n);
        for (var k = 0; k < n; k++)
        {
            var row = group.Select(p => p.Samples[k % p.Samples.Count]).OrderBy(v => v).ToList();
            merged.Add(row[row.Count / 2]);
        }
        merged.Sort();
        var rep = group[0];
        return rep with
        {
            DepthM = depthM,
            Samples = merged,
            Quantiles = new[] { 0.025, 0.5, 0.975 }
                .Select(p => new QuantilePoint(p, CalibrationCurve.Quantile(merged, p))).ToList()
        };
    }

    private static Dictionary<string, List<PreparedAnchor>> PoolMap(List<PreparedAnchor> ordered)
    {
        var map = new Dictionary<string, List<PreparedAnchor>>();
        var blocks = ordered.Select(p => new List<PreparedAnchor> { p }).ToList();
        var changed = true;
        while (changed && blocks.Count > 1)
        {
            changed = false;
            for (var i = 0; i < blocks.Count - 1; i++)
            {
                var a = Median(blocks[i].Last());
                var b = Median(blocks[i + 1].First());
                if (a > b)
                {
                    var merged = blocks[i].Concat(blocks[i + 1]).ToList();
                    blocks[i] = merged;
                    blocks.RemoveAt(i + 1);
                    changed = true;
                    break;
                }
            }
        }
        foreach (var block in blocks.Where(b => b.Count > 1))
        foreach (var member in block)
            map[member.Anchor.Id] = block;
        return map;
    }

    private static UncertaintyBand EmptyBand(ModelSegment segment) => new()
    {
        SegmentId = segment.Id,
        Points = new(),
        EffectiveTopDepthM = segment.TopDepthM,
        EffectiveBottomDepthM = segment.BottomDepthM
    };

    private static UncertaintyBand SinglePointBand(ModelSegment segment, PreparedAnchor anchor) => new()
    {
        SegmentId = segment.Id,
        Points = new()
        {
            new(anchor.DepthM,
                anchor.Quantiles.First(q => q.P == 0.025).CalYrBP,
                anchor.Quantiles.First(q => q.P == 0.5).CalYrBP,
                anchor.Quantiles.First(q => q.P == 0.975).CalYrBP)
        },
        EffectiveTopDepthM = segment.TopDepthM,
        EffectiveBottomDepthM = segment.BottomDepthM
    };

    private static double Median(PreparedAnchor p) => p.Quantiles.First(q => q.P == 0.5).CalYrBP;
    private static double Percentile(List<double> sorted, double p)
    {
        var idx = (int)Math.Round(p * (sorted.Count - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
