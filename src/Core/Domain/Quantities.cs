using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoDepth.Core.Domain;

/// <summary>深度坐标，基准例如 cm-below-seafloor (cmbsf)。</summary>
public readonly record struct Depth(double Value, string Unit, string Datum)
{
    public const string Cmbsf = "cmbsf";
    public static Depth CmBelowSeafloor(double v) => new(v, "cm", Cmbsf);
    public string Format() => Value.ToString("G", CultureInfo.InvariantCulture) + " " + Unit + " [" + Datum + "]";
}

/// <summary>年龄，时间基准例如 ka BP（Before Present, 1950）。</summary>
public readonly record struct Age(double Value, string Unit, string Scale)
{
    public const string KaBP = "ka BP";
    public static Age KaBp(double v) => new(v, "ka", KaBP);
    public string Format() => Value.ToString("G", CultureInfo.InvariantCulture) + " " + Unit + " [" + Scale + "]";
}

/// <summary>沉积速率，cm/ka。</summary>
public readonly record struct SedRate(double CmPerKa)
{
    public string Format() => CmPerKa.ToString("G4", CultureInfo.InvariantCulture) + " cm/ka";
}

/// <summary>
/// 年龄的概率分布。绝不允许只用均值替代：导入层保留原始表示（raw JSON），
/// 派生层可规范化为加权样本。quantiles 为 {0.025,0.5,0.975} 之类的分位数点。
/// </summary>
public sealed record AgeDistribution
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "samples";
    [JsonPropertyName("unit")] public string Unit { get; init; } = "ka";
    [JsonPropertyName("scale")] public string Scale { get; init; } = Age.KaBP;
    [JsonPropertyName("samples")] public double[] Samples { get; init; } = System.Array.Empty<double>();
    [JsonPropertyName("weights")] public double[]? Weights { get; init; }
    [JsonPropertyName("quantiles")] public Dictionary<string, double>? Quantiles { get; init; }
    /// <summary>原始导入表示（字符串/对象），规范化失败时也不丢弃。</summary>
    [JsonPropertyName("raw")] public JsonElement? Raw { get; init; }

    public bool HasSamples => Samples.Length > 0;

    public double Quantile(double p)
    {
        if (!HasSamples) throw new InvalidOperationException("distribution has no normalized samples");
        var idx = Samples
            .Select((s, i) => (s, w: Weights?[i] ?? 1.0 / Samples.Length))
            .OrderBy(t => t.s).ToArray();
        double target = p * idx.Sum(t => t.w);
        double acc = 0, prev = idx[0].s;
        foreach (var (s, w) in idx)
        {
            if (acc + w >= target) return s;
            acc += w; prev = s;
        }
        return idx[^1].s;
    }

    public double Median => Quantile(0.5);

    public static AgeDistribution FromRawSamples(IEnumerable<double> samples, string unit = "ka", string scale = Age.KaBP)
        => new() { Kind = "samples", Unit = unit, Scale = scale, Samples = samples.ToArray() };
}

/// <summary>带单位与基准的标量值；无法规范化时 Value 为 null 并保留 Raw 与诊断。</summary>
public sealed record MeasuredValue
{
    public double? Value { get; init; }
    public string? Unit { get; init; }
    public string? DatumOrScale { get; init; }
    public string? RawText { get; init; }
    public string? Diagnostic { get; init; }
    public bool IsConvertible => Value.HasValue && string.IsNullOrEmpty(Diagnostic);
}
