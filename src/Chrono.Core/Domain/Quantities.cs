using System.Globalization;

namespace Chrono.Core.Domain;

/// <summary>结果三态：成功 / 格式损坏 / 语义无解 / 预算耗尽（后两者在引擎层细分）。</summary>
public enum OutcomeKind { Ok, FormatCorrupt, Incomplete, SemanticUnsolvable, BudgetExhausted }

/// <summary>带位置的诊断。位置优先使用稳定身份与字段名，导入时可回退到 JSON Pointer。</summary>
public sealed record Diagnostic(string Code, string Message, string? MaterialId = null, string? Field = null, string? Pointer = null)
{
    public string Location => MaterialId is not null && Field is not null
        ? $"{MaterialId}#{Field}"
        : Pointer ?? MaterialId ?? Field ?? "?";

    public override string ToString() => $"[{Code}] {Location}: {Message}";
}

/// <summary>无法转换的量：保留在原位置，绝不丢弃。</summary>
public sealed record UnconvertedValue(string MaterialId, string Field, string Raw, string Reason)
{
    public string Display => $"{MaterialId}#{Field}=\"{Raw}\" ({Reason})";
}

/// <summary>
/// 原始量：导入时保留原始表示，规范化值只存在于派生层。
/// 无法转换时 Converted 为 null，Diagnostic 说明原因，对象仍留在材料原位置。
/// </summary>
public sealed record RawQuantity(string Raw, string? Unit, double? Converted, string? NormalizedUnit, Diagnostic? Problem)
{
    public bool ConvertedOk => Converted.HasValue && Problem is null;

    public static RawQuantity Parse(string? raw, string? declaredUnit, IReadOnlyDictionary<string, double> factors,
        string normalizedUnit, string materialId, string field)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
            return new RawQuantity(raw ?? "", declaredUnit, null, normalizedUnit,
                new Diagnostic("INCOMPLETE_VALUE", "值为空，不得用默认值伪装成功", materialId, field));

        var unit = declaredUnit?.Trim();
        // 内联单位，如 "12.40 m" / "320 cm"
        var num = text;
        var sp = text.LastIndexOf(' ');
        if (sp > 0 && double.TryParse(text[..sp], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            num = text[..sp];
            unit ??= text[(sp + 1)..].Trim();
        }
        if (unit is null)
            return new RawQuantity(raw!, declaredUnit, null, normalizedUnit,
                new Diagnostic("MISSING_UNIT", "缺少单位/坐标基准，无法规范化", materialId, field));
        if (!factors.TryEquals(unit, out var factor))
            return new RawQuantity(raw!, unit, null, normalizedUnit,
                new Diagnostic("UNCONVERTIBLE_UNIT", $"单位 {unit} 无法转换到 {normalizedUnit}", materialId, field));
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new RawQuantity(raw!, unit, null, normalizedUnit,
                new Diagnostic("UNPARSEABLE_NUMBER", $"无法解析数值 {num}", materialId, field));
        if (!double.IsFinite(v))
            return new RawQuantity(raw!, unit, null, normalizedUnit,
                new Diagnostic("NON_FINITE", "数值非有限值", materialId, field));
        return new RawQuantity(raw!, unit, v * factor, normalizedUnit, null);
    }
}

internal static class UnitLookup
{
    public static bool TryEquals(this IReadOnlyDictionary<string, double> d, string unit, out double factor)
    {
        foreach (var (k, v) in d)
            if (string.Equals(k.Trim(), unit.Trim(), StringComparison.OrdinalIgnoreCase)) { factor = v; return true; }
        factor = 0;
        return false;
    }
}

/// <summary>深度单位换算（规范化为米）。</summary>
public static class DepthUnits
{
    public static readonly Dictionary<string, double> ToMeters = new()
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["mbsf"] = 1.0,
        ["cm"] = 0.01, ["centimeter"] = 0.01, ["centimeters"] = 0.01,
        ["mm"] = 0.001,
    };
}

/// <summary>年龄单位换算（规范化为 ka BP，BP=1950）。放射性碳原始年龄不做未经校准的换算。</summary>
public static class AgeUnits
{
    public static readonly Dictionary<string, double> ToKa = new()
    {
        ["ka bp"] = 1.0, ["ka"] = 1.0, ["kyr bp"] = 1.0, ["kyr"] = 1.0,
        ["cal ka bp"] = 1.0, ["cal ka"] = 1.0, ["cal kyr bp"] = 1.0,
        ["a bp"] = 0.001, ["yr bp"] = 0.001, ["years bp"] = 0.001,
        ["cal a bp"] = 0.001, ["cal yr bp"] = 0.001, ["cal years bp"] = 0.001,
        ["ma bp"] = 1000.0, ["ma"] = 1000.0,
    };

    /// <summary>放射性碳单位：只保留原始表示，禁止与日历年混算。</summary>
    public static bool IsRadiocarbonRaw(string? unit) =>
        unit is not null && (unit.Contains("14c", StringComparison.OrdinalIgnoreCase)
                             || unit.Contains("radiocarbon", StringComparison.OrdinalIgnoreCase)
                             || unit.Trim().Equals("bp", StringComparison.OrdinalIgnoreCase));
}
