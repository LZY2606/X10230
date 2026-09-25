using System.Globalization;
using System.Text.Json.Serialization;

namespace Chrono.Core.Domain;

/// <summary>深度，带原始表示与坐标基准。内部规范化为米，方向向下为正。</summary>
public sealed record Depth
{
    public required double Meters { get; init; }
    public required string RawText { get; init; }
    public required string Unit { get; init; }
    public required string Datum { get; init; }

    public static readonly IReadOnlyDictionary<string, double> Factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0,
        ["cm"] = 0.01,
        ["mm"] = 0.001,
    };

    public static Depth Parse(string text, string datum, string? unitHint = null)
    {
        var raw = text.Trim();
        var unit = unitHint ?? "m";
        var numPart = raw;
        var split = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsLetter(c) || c == '‰' || c == '°') { split = i; break; }
        }
        if (split >= 0)
        {
            numPart = raw[..split].Trim();
            unit = raw[split..].Trim();
        }
        if (!Factors.TryGetValue(unit, out var factor))
            throw new FormatException($"未知深度单位 '{unit}'（支持 m/cm/mm）");
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"深度无法解析: '{raw}'");
        return new Depth
        {
            Meters = value * factor,
            RawText = raw,
            Unit = unit.ToLowerInvariant(),
            Datum = datum,
        };
    }

    public string Display => string.Create(CultureInfo.InvariantCulture, $"{Meters * 100:0.##} cm ({RawText} @ {Datum})");
}

/// <summary>时间基准（年）。cal BP 以 1950 CE 为零，BP 口径必须显式。</summary>
public sealed record AgeEpoch
{
    public required string RawText { get; init; }
    public required string Unit { get; init; }
    public required string Scale { get; init; }

    /// <summary>规范化为距 1950 年的日历年（cal BP）。</summary>
    public double ToCalBpYears(double yearsValue)
    {
        return Scale switch
        {
            "cal BP" => yearsValue,
            "BCE/CE" => 1950 - yearsValue, // yearsValue: 正=CE, 负=BCE
            _ => throw new FormatException($"未知年龄时间基准 '{Scale}'"),
        };
    }
}

/// <summary>一次放射性年龄测量：原始表示保留，规范化只在派生层发生。</summary>
public sealed record AgeMeasure
{
    public required double Years14C { get; init; }
    public required double SigmaYears { get; init; }
    public required string RawText { get; init; }
    public string Material { get; init; } = "";
    public string LabCode { get; init; } = "";
    /// <summary>实验室分布口径：normal 或 longtail（长尾混合）。</summary>
    public string LabDistribution { get; init; } = "normal";
}

/// <summary>储层/碳库偏移：独立分布，不并入测量误差。</summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required double MeanYears { get; init; }
    public required double SigmaYears { get; init; }
    public required string RawText { get; init; }
    public string Scope { get; init; } = "global";
}

/// <summary>带单位的沉积速率。</summary>
public readonly record struct SedRate(double MPerKyr)
{
    public double CmPerKyr => MPerKyr * 100.0;
    public string Display => string.Create(CultureInfo.InvariantCulture, $"{CmPerKyr:0.###} cm/ka ({MPerKyr:0.######} m/yr)");
}
