using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

/// <summary>沉积速率区间：逐样本计算，得到中位数与 95% 区间；方向倒挂显式标记。</summary>
public static class RateFactory
{
    public static (List<RateInterval> Rates, string? Note) LinearRates(string segmentId, List<PreparedAnchor> ordered)
    {
        var rates = new List<RateInterval>();
        for (var i = 0; i < ordered.Count - 1; i++)
            rates.Add(Interval(segmentId, ordered[i], ordered[i + 1], pool: false));
        return (rates, null);
    }

    /// <summary>PAVA：把违反单调的相邻锚点合并为等龄块，块内不报告速率。</summary>
    public static (List<RateInterval> Rates, string? Note) PavaRates(string segmentId, List<PreparedAnchor> input)
    {
        var blocks = PoolViolators(input);
        var pooled = blocks.Count < input.Count;
        var rates = new List<RateInterval>();
        for (var i = 0; i < blocks.Count - 1; i++)
            rates.Add(Interval(segmentId, blocks[i].Last(), blocks[i + 1].First(), pool: false,
                effectiveTop: blocks[i].Min(p => p.DepthM), effectiveBottom: blocks[i + 1].Max(p => p.DepthM)));
        foreach (var block in blocks.Where(b => b.Count > 1))
        {
            var top = block.Min(p => p.DepthM);
            var bottom = block.Max(p => p.DepthM);
            rates.Add(new RateInterval
            {
                SegmentId = segmentId,
                FromDepthM = top,
                ToDepthM = bottom,
                FromAnchorId = string.Join("+", block.Select(p => p.Anchor.Id)),
                ToAnchorId = string.Join("+", block.Select(p => p.Anchor.Id)),
                MedianCmPerKyr = 0,
                LowCmPerKyr = 0,
                HighCmPerKyr = 0,
                AgeDirectionOk = true,
                EffectiveTopDepthM = top,
                EffectiveBottomDepthM = bottom,
                InvalidationReason = "PAVA 合并等龄块：块内不定义沉积速率（年龄被约束为相等）"
            });
        }
        var pooledAnchors = blocks.Where(b => b.Count > 1).Sum(b => b.Count);
        var note = pooled
            ? $"PAVA 将 {pooledAnchors} 个违反单调的相邻锚点合并为等龄块（{blocks.Count(b => b.Count > 1)} 块），未删除任何材料"
            : null;
        return (rates.OrderBy(r => r.FromDepthM).ToList(), note);
    }

    private static List<List<PreparedAnchor>> PoolViolators(List<PreparedAnchor> input)
    {
        var blocks = input.Select(p => new List<PreparedAnchor> { p }).ToList();
        var changed = true;
        while (changed && blocks.Count > 1)
        {
            changed = false;
            for (var i = 0; i < blocks.Count - 1; i++)
            {
                var a = blocks[i].Last();
                var b = blocks[i + 1].First();
                if (Median(a) > Median(b)) // 下覆更年轻 => 倒置，合并
                {
                    blocks[i].AddRange(blocks[i + 1]);
                    blocks.RemoveAt(i + 1);
                    changed = true;
                    break;
                }
            }
        }
        return blocks;
    }

    private static RateInterval Interval(string segmentId, PreparedAnchor younger, PreparedAnchor older,
        bool pool, double? effectiveTop = null, double? effectiveBottom = null)
    {
        var depthSpanM = Math.Max(1e-9, older.DepthM - younger.DepthM);
        var n = Math.Min(younger.Samples.Count, older.Samples.Count);
        if (n < 8)
            return Invalid(segmentId, younger, older, effectiveTop, effectiveBottom,
                "有效样本不足，无法构造速率区间（不以点估计替代）");

        var rates = new List<double>(n);
        var positiveDirections = 0;
        for (var k = 0; k < n; k++)
        {
            var youngerAge = younger.Samples[k % younger.Samples.Count];
            var olderAge = older.Samples[k % older.Samples.Count];
            var dtYr = olderAge - youngerAge;
            if (dtYr > 0)
            {
                positiveDirections++;
                // cm/kyr = (m * 100 cm/m) / (yr / 1000)
                rates.Add(depthSpanM * 100.0 / (dtYr / 1000.0));
            }
        }
        var directionOk = positiveDirections >= 0.95 * n;
        if (rates.Count < 8)
            return Invalid(segmentId, younger, older, effectiveTop, effectiveBottom,
                $"倒置样本占比过高（仅 {positiveDirections}/{n} 满足下覆更老），速率区间不可信");

        rates.Sort();
        return new RateInterval
        {
            SegmentId = segmentId,
            FromDepthM = younger.DepthM,
            ToDepthM = older.DepthM,
            FromAnchorId = younger.Anchor.Id,
            ToAnchorId = older.Anchor.Id,
            MedianCmPerKyr = MedianRate(rates),
            LowCmPerKyr = Percentile(rates, 0.025),
            HighCmPerKyr = Percentile(rates, 0.975),
            AgeDirectionOk = directionOk,
            EffectiveTopDepthM = effectiveTop ?? younger.DepthM,
            EffectiveBottomDepthM = effectiveBottom ?? older.DepthM
        };
    }

    private static RateInterval Invalid(string segmentId, PreparedAnchor younger, PreparedAnchor older,
        double? effectiveTop, double? effectiveBottom, string reason) => new()
    {
        SegmentId = segmentId,
        FromDepthM = younger.DepthM,
        ToDepthM = older.DepthM,
        FromAnchorId = younger.Anchor.Id,
        ToAnchorId = older.Anchor.Id,
        MedianCmPerKyr = 0,
        LowCmPerKyr = 0,
        HighCmPerKyr = 0,
        AgeDirectionOk = false,
        EffectiveTopDepthM = effectiveTop ?? younger.DepthM,
        EffectiveBottomDepthM = effectiveBottom ?? older.DepthM,
        InvalidationReason = reason
    };

    private static double Median(PreparedAnchor p) => p.Quantiles.First(q => q.P == 0.5).CalYrBP;
    private static double MedianRate(List<double> sorted) => Percentile(sorted, 0.5);
    private static double Percentile(List<double> sorted, double p)
    {
        var idx = (int)Math.Round(p * (sorted.Count - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
