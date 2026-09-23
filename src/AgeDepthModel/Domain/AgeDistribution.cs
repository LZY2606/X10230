using System.Text.Json;
using AgeDepthModel.Domain;

namespace AgeDepthModel.Domain;

/// <summary>
/// 年龄分布。导入层保留原始表示（Raw / 原始单位），派生层才规范化为 ka。
/// 分布永远以分位数/样本形式参与计算，不允许被均值替代。
/// </summary>
public sealed record AgeDistribution(
    string Type,
    string Unit,
    IReadOnlyList<double>? Samples,
    IReadOnlyList<(double P, double V)>? Quantiles,
    string Raw)
{
    public double Quantile(double p)
    {
        if (Samples is { Count: > 0 } samples)
        {
            var sorted = SamplesSorted(samples);
            var pos = p * (sorted.Count - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, sorted.Count - 1);
            var frac = pos - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }
        if (Quantiles is { Count: > 0 } qs)
        {
            var ordered = qs.OrderBy(q => q.P).ToList();
            if (p <= ordered[0].P) return ordered[0].V;
            if (p >= ordered[^1].P) return ordered[^1].V;
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var (p0, v0) = ordered[i];
                var (p1, v1) = ordered[i + 1];
                if (p >= p0 && p <= p1)
                    return v0 + (v1 - v0) * ((p - p0) / (p1 - p0));
            }
        }
        throw new InvalidOperationException($"分布 {Raw} 既无样本也无分位数。");
    }

    public double Median => Quantile(0.5);

    private static IReadOnlyList<double> SamplesSorted(IReadOnlyList<double> s) =>
        s is List<double> l && IsSorted(l) ? l : s.OrderBy(x => x).ToList();

    private static bool IsSorted(List<double> l)
    {
        for (var i = 1; i < l.Count; i++) if (l[i] < l[i - 1]) return false;
        return true;
    }

    /// <summary>解析导入 JSON。无法解析时返回 CorruptFormat 诊断，原值留在原位置。</summary>
    public static bool TryParse(string json, out AgeDistribution? dist, out Diagnostic? diagnostic)
    {
        dist = null;
        diagnostic = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
            var unit = root.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
            List<double>? samples = null;
            List<(double, double)>? quantiles = null;
            if (root.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.Array)
                samples = s.EnumerateArray().Select(e => e.GetDouble()).ToList();
            if (root.TryGetProperty("quantiles", out var q) && q.ValueKind == JsonValueKind.Array)
                quantiles = q.EnumerateArray()
                    .Select(e => (e.GetProperty("p").GetDouble(), e.GetProperty("v").GetDouble()))
                    .ToList();
            if (samples is null && quantiles is null)
            {
                diagnostic = new Diagnostic("Distribution.Empty",
                    "分布既无 samples 也无 quantiles，保留原始表示，不参与派生计算。", "distribution");
                return false;
            }
            dist = new AgeDistribution(type, unit, samples, quantiles, json);
            return true;
        }
        catch (JsonException ex)
        {
            diagnostic = new Diagnostic("Distribution.Corrupt",
                $"分布 JSON 损坏: {ex.Message}", "distribution");
            return false;
        }
    }

    /// <summary>支持的单位到 ka 的换算因子；不支持的单位返回 null（留在原位置并附诊断）。</summary>
    public static double? UnitToKaFactor(string unit) => unit switch
    {
        "ka" => 1.0,
        "calBP" or "BP" or "yrBP" => 0.001,
        "Ma" => 1000.0,
        "a" or "yr" => 0.001,
        _ => null
    };

    /// <summary>派生层规范化：换算到 ka 并扣除储层偏移（年）。原始表示不变。</summary>
    public AgeDistribution NormalizeToKa(double reservoirOffsetYears)
    {
        var factor = UnitToKaFactor(Unit)
            ?? throw new InvalidOperationException($"单位 '{Unit}' 无法换算为 ka。");
        var offsetKa = reservoirOffsetYears * 0.001;
        IReadOnlyList<double>? samples = Samples?.Select(v => v * factor - offsetKa).ToList();
        IReadOnlyList<(double P, double V)>? quantiles = Quantiles?
            .Select(q => (q.P, q.V * factor - offsetKa)).ToList();
        return this with { Unit = "ka", Samples = samples, Quantiles = quantiles };
    }
}
