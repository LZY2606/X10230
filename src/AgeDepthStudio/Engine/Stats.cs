using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Engine;

public static class Stats
{
    public static double Quantile(double[] xs, double p)
    {
        var s = xs.OrderBy(v => v).ToArray();
        if (s.Length == 1) return s[0];
        var h = (s.Length - 1) * p;
        var lo = (int)Math.Floor(h);
        var frac = h - lo;
        return s[lo] * (1 - frac) + s[Math.Min(lo + 1, s.Length - 1)] * frac;
    }

    public static double Mean(double[] xs) => xs.Average();
    public static double Std(double[] xs)
    {
        var m = Mean(xs);
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Length);
    }

    /// <summary>储层偏移改正 + 参数膨胀，全部在派生层进行，原始样本永不改写。</summary>
    public static double[] Correct(IEnumerable<double> raw, ReservoirOffset? off, RuleParams p)
    {
        var z = raw.Select(s => s - (off?.OffsetKa ?? 0)).ToArray();
        var mu = Mean(z);
        var infl = p.AgeInflation;
        var extra = off?.SigmaKa ?? 0;
        // 确定性宽化：储层偏移的不确定度 + 参数年龄膨胀，均不触碰原始样本。
        return z.Select(s =>
        {
            var d = s - mu;
            var sign = d < 0 ? -1 : 1;
            var widened = sign * Math.Sqrt(d * d * infl * infl + extra * extra);
            return mu + widened;
        }).ToArray();
    }
}
