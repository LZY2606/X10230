using System.Globalization;
using System.Text.Json.Serialization;

namespace ChronoCore;

/// <summary>深度坐标基准（raw 表示随材料原文保留，规范化层才换算）。</summary>
public enum DepthUnit { M, Cm, Mm, Ft }
public enum AgeUnit { CalBp, BceCe, KaBp }
public enum AgeBasis { Calibrated14C, Radiocarbon14C, UTh, Pb210, TephraCorrelation, Biostrat, Picked }

/// <summary>原始表示始终保留；value/unit 是派生规范化结果，无法转换时 value=null 并保留诊断。</summary>
public sealed record Measure(
    string Raw,
    double? Value,
    string NormalizedUnit,
    string Datum,
    IReadOnlyList<string> Diagnostics)
{
    public bool IsConverted => Value.HasValue;
    public static Measure Fail(string raw, string datum, string reason) =>
        new(raw, null, string.Empty, datum, new[] { reason });
}

public readonly record struct DepthM(double Value)
{
    public double Cm => Value * 100.0;
    public static DepthM operator +(DepthM a, double b) => new(a.Value + b);
}

public readonly record struct AgeCalBp(double Value)
{
    public double Ka => Value / 1000.0;
}

/// <summary>解析深度：识别 "m CSF-A" 等基准（CSF-A 岩芯深度、CCSF 复合深度、BSF 海底以下）。</summary>
public static class DepthParser
{
    public static Measure Parse(string raw)
    {
        var diag = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return Measure.Fail(raw ?? "", "", "深度为空");
        var datum = ExtractDatum(raw, out var body);
        var (num, unit) = ExtractNumberUnit(body);
        if (num is null)
        {
            var reason = unit is null ? "无法识别深度数字" : "无法识别深度单位";
            return Measure.Fail(raw, datum, reason);
        }
        var meters = ToMeters(num.Value, unit!);
        if (meters is null)
            return Measure.Fail(raw, datum, $"不支持的深度单位 '{unit}'");
        if (meters.Value < 0)
            diag.Add($"深度为负（{raw}），保留原值但标为越界");
        return new Measure(raw, meters.Value, "m", datum, diag);
    }

    private static string ExtractDatum(string raw, out string body)
    {
        var s = raw.Trim();
        var upper = s.ToUpperInvariant();
        foreach (var d in new[] { "CSF-A", "CCSF-A", "CCSF", "CSF", "BSF", "MBSF", "MCD" })
        {
            var i = upper.IndexOf(d, StringComparison.Ordinal);
            if (i >= 0)
            {
                body = (s[..i] + s[(i + d.Length)..]).Trim(' ', '-', ':', '(');
                return d;
            }
        }
        body = s;
        return "";
    }

    private static (double?, string?) ExtractNumberUnit(string body)
    {
        body = body.Trim();
        int k = 0;
        while (k < body.Length && (char.IsDigit(body[k]) || body[k] is '.' or '-' or '+' or ',' or 'e' or 'E' or ' '))
            k++;
        var numText = body[..k].Trim().Replace(",", ".");
        var unitText = body[k..].Trim().Trim('(', ')').ToLowerInvariant();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return (null, null);
        if (unitText.Length == 0)
            unitText = "m";
        else if (unitText is "meters" or "metres" or "meter")
            unitText = "m";
        else if (unitText is "centimeters" or "centimetres" or "centimeter")
            unitText = "cm";
        else if (unitText is "millimeters" or "millimetres")
            unitText = "mm";
        else if (unitText is "feet" or "foot" or "ft")
            unitText = "ft";
        return (n, unitText);
    }

    private static double? ToMeters(double v, string unit) => unit switch
    {
        "m" => v,
        "cm" => v / 100.0,
        "mm" => v / 1000.0,
        "ft" => v * 0.3048,
        _ => null
    };
}

/// <summary>解析年龄：支持 cal BP、ka BP 与 BCE/CE 纪年。放射性 14C 年龄走储层校正，不在这里替换。</summary>
public static class AgeParser
{
    public static Measure Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Measure.Fail(raw ?? "", "", "年龄为空");
        var s = raw.Trim();
        var datum = "cal BP";
        var body = s;
        var upper = s.ToUpperInvariant();
        if (upper.Contains("BCE") || upper.Contains("BC"))
        {
            datum = "BCE/CE";
            body = upper.Replace("BCE", "").Replace("BC", "").Trim();
        }
        else if (upper.Contains("CE") || upper.Contains("AD"))
        {
            datum = "BCE/CE";
            body = upper.Replace("CE", "").Replace("AD", "").Trim();
        }

        int k = 0;
        while (k < body.Length && (char.IsDigit(body[k]) || body[k] is '.' or '-' or '+' or ',' or '~' or ' '))
            k++;
        var numText = body[..k].Trim().Replace(",", ".").TrimStart('~');
        var rest = body[k..].Trim().ToLowerInvariant();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return Measure.Fail(raw, datum, "无法识别年龄数字");
        var ka = rest.StartsWith("ka") || rest.StartsWith("kyr") || rest.StartsWith("ky");
        double calBp;
        if (datum == "BCE/CE")
        {
            var bce = upper.Contains("BCE") || upper.Contains("BC");
            var year = bce ? 1950 + n : 1950 - n;
            calBp = year;
        }
        else
            calBp = ka ? n * 1000.0 : n;
        return new Measure(raw, calBp, "cal a BP", datum, Array.Empty<string>());
    }

    /// <summary>BCE/CE 文本仅用于展示，计算一律使用 cal BP。</summary>
    public static string FormatCalBp(double calBp)
    {
        var year = 1950 - calBp;
        if (year <= 0)
            return $"{(-year + 1):0} BCE ({calBp:0.#} cal BP)";
        return $"{year:0} CE ({calBp:0.#} cal BP)";
    }
}
