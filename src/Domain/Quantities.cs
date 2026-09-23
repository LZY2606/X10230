using System.Globalization;

namespace DepthAgeReview.Domain;

/// <summary>
/// 一个带物理维度、原始表示、规范化值和基准(单位/坐标/时间基准)的量。
/// 导入层只做解析；无法转换的值保留在 <see cref="Raw"/>，<see cref="Canonical"/> 为 null，
/// 并由 <see cref="Convertible"/> 携带诊断——绝不为了画图而静默丢弃或用默认值顶替。
/// </summary>
public sealed record Quantity(string Raw, string Unit, double? Canonical, string Dimension, bool Convertible, string? Diagnostic = null)
{
    public static Quantity ParseDepth(string raw, string? unit, DepthDatum? datum)
    {
        if (!TryParseNumber(raw, out var value))
            return new Quantity(raw, unit ?? "", null, "depth", false, $"无法把深度表示 '{raw}' 解析为数值（位置：锚点/分段深度）");
        var u = (unit ?? datum?.DepthUnit ?? "cm").Trim();
        if (!UnitSystem.DepthToCm.TryGetValue(u, out var factor))
            return new Quantity(raw, u, null, "depth", false, $"未知深度单位 '{u}'（原始值 '{raw}' 保留，不规范化）");
        return new Quantity(raw, u, value * factor, "depth", true) with { };
    }

    public static Quantity ParseAge(string raw, string? unit)
    {
        if (!TryParseNumber(raw, out var value))
            return new Quantity(raw, unit ?? "", null, "age", false, $"无法把年龄表示 '{raw}' 解析为数值");
        var u = (unit ?? "cal a BP").Trim();
        if (!UnitSystem.AgeToCalABP.TryGetValue(u, out var factor))
            return new Quantity(raw, u, null, "age", false, $"未知年龄单位/时间基准 '{u}'（原始值 '{raw}' 保留）");
        return new Quantity(raw, u, value * factor, "age", true);
    }

    public static Quantity Cm(double v) => new(v.ToString("0.###", CultureInfo.InvariantCulture), "cm", v, "depth", true);
    public static Quantity CalAbp(double v) => new(v.ToString("0.###", CultureInfo.InvariantCulture), "cal a BP", v, "age", true);

    internal static bool TryParseNumber(string raw, out double value)
    {
        raw = raw.Trim();
        if (raw.EndsWith("?") || raw.EndsWith("？"))
        {
            value = default;
            return false;
        }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public override string ToString() => $"{Raw} {Unit}";
}

public static class UnitSystem
{
    /// <summary>各深度单位到 cm 的换算因子（坐标基准跟随 DepthDatum）。</summary>
    public static readonly IReadOnlyDictionary<string, double> DepthToCm = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["cm"] = 1.0, ["m"] = 100.0, ["mm"] = 0.1, ["mbsf"] = 100.0,
    };

    /// <summary>各年龄单位/时间基准到 cal a BP（BP=1950）的换算因子。</summary>
    public static readonly IReadOnlyDictionary<string, double> AgeToCalABP = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["cal a BP"] = 1.0, ["cal yr BP"] = 1.0, ["a BP"] = 1.0, ["yr BP"] = 1.0, ["BP"] = 1.0,
        ["cal ka BP"] = 1000.0, ["ka BP"] = 1000.0, ["ka"] = 1000.0,
        ["BCE"] = double.NaN, // 需要坐标年份锚点，语义上不等价于线性缩放，显式拒绝
        ["14C a BP"] = double.NaN, // 放射性碳原始年龄，必须经校正曲线，不允许直接缩放
    };
}
