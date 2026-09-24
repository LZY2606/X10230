using System.Globalization;

namespace AgeDepth.Core;

/// <summary>带位置的诊断：指出哪条材料的哪个字段出了问题。</summary>
public sealed record Diagnostic(string Path, string Severity, string Message);

/// <summary>
/// 领域量：始终保留原始表示（Raw/Unit/Datum），规范化值放在派生层。
/// 无法转换时 Normalized 为 null 并附诊断，原值留在原位。
/// </summary>
public sealed class Quantity
{
    public string Raw { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Datum { get; set; } = "";
    public double? Normalized { get; set; }
    public Diagnostic? Diagnostic { get; set; }

    public string Display =>
        Normalized is { } v
            ? $"{Raw} {Unit} {Datum} (= {v:G6} {(UnitConverter.IsAgeUnit(Unit) ? "ka" : "m")})".Trim()
            : $"{Raw} {Unit} {Datum} (不可转换)".Trim();
}

public static class UnitConverter
{
    static readonly Dictionary<string, double> DepthFactors = new(StringComparer.OrdinalIgnoreCase)
    { ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048 };

    static readonly Dictionary<string, double> AgeFactors = new(StringComparer.OrdinalIgnoreCase)
    { ["ka"] = 1.0, ["kyr"] = 1.0, ["ma"] = 1000.0, ["yr"] = 0.001, ["a"] = 0.001 };

    public static bool IsAgeUnit(string unit) => AgeFactors.ContainsKey(unit ?? "");
    public static bool IsDepthUnit(string unit) => DepthFactors.ContainsKey(unit ?? "");

    public static double? AgeFactor(string unit) =>
        AgeFactors.TryGetValue(unit ?? "", out var f) ? f : null;

    public static Quantity Depth(string? raw, string? unit, string? datum, string path)
        => Build(raw, unit, datum, path, DepthFactors, "深度");

    public static Quantity Age(string? raw, string? unit, string? datum, string path)
        => Build(raw, unit, datum, path, AgeFactors, "年龄");

    static Quantity Build(string? raw, string? unit, string? datum, string path,
        Dictionary<string, double> factors, string label)
    {
        var q = new Quantity { Raw = raw ?? "", Unit = unit ?? "", Datum = datum ?? "" };
        if (q.Raw.Length == 0 && q.Unit.Length == 0) return q; // 未声明的可选量
        if (!double.TryParse(q.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            q.Diagnostic = new Diagnostic(path, "error", $"{label}数值无法解析: '{q.Raw}'，原值保留在位");
            return q;
        }
        if (!factors.TryGetValue(q.Unit, out var factor))
        {
            q.Diagnostic = new Diagnostic(path, "error",
                $"{label}单位无法转换: '{q.Unit}'，原值保留在位，未做规范化");
            return q;
        }
        q.Normalized = value * factor;
        return q;
    }
}
