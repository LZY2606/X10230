using System.Globalization;

namespace DepthAge.Core;

public enum QuantityKind { Depth, Age, Offset, Rate }

/// <summary>
/// 领域量一律携带单位（深度还携带坐标基准 Datum）。原始表示永远保留在材料层，
/// 只有派生层（引擎）才调用 TryConvert 做规范化；无法转换时留在原位置并附诊断。
/// </summary>
public sealed record Quantity(double Value, string Unit, string? Datum = null)
{
    public override string ToString() =>
        Datum is null
            ? $"{Value.ToString("R", CultureInfo.InvariantCulture)} {Unit}"
            : $"{Value.ToString("R", CultureInfo.InvariantCulture)} {Unit} ({Datum})";
}

public static class UnitRegistry
{
    public const string CanonicalDepthUnit = "m";
    public const string CanonicalAgeUnit = "ka";
    public const string CanonicalOffsetUnit = "ka";
    public const string CanonicalRateUnit = "m/ka";

    public static string CanonicalUnit(QuantityKind kind) => kind switch
    {
        QuantityKind.Depth => CanonicalDepthUnit,
        QuantityKind.Age => CanonicalAgeUnit,
        QuantityKind.Offset => CanonicalOffsetUnit,
        QuantityKind.Rate => CanonicalRateUnit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>转换到规范单位（深度 m、年龄/偏移 ka、速率 m/ka）。失败时不抛异常，由调用方保留原值。</summary>
    public static bool TryConvert(Quantity q, QuantityKind kind, out Quantity result, out string? error)
    {
        result = q;
        error = null;
        var unit = (q.Unit ?? string.Empty).Trim().ToLowerInvariant();
        double factor = unit switch
        {
            // 深度
            "m" or "meter" or "meters" when kind == QuantityKind.Depth => 1.0,
            "cm" when kind == QuantityKind.Depth => 0.01,
            "mm" when kind == QuantityKind.Depth => 0.001,
            "ft" or "feet" when kind == QuantityKind.Depth => 0.3048,
            // 年龄 / 储层偏移
            "ka" or "kyr" when kind is QuantityKind.Age or QuantityKind.Offset => 1.0,
            "yr" or "a" or "year" or "years" when kind is QuantityKind.Age or QuantityKind.Offset => 0.001,
            "ma" or "myr" when kind is QuantityKind.Age or QuantityKind.Offset => 1000.0,
            // 沉积速率
            "m/ka" when kind == QuantityKind.Rate => 1.0,
            "cm/yr" when kind == QuantityKind.Rate => 10.0,
            "mm/yr" when kind == QuantityKind.Rate => 1.0,
            _ => double.NaN
        };
        if (double.IsNaN(factor))
        {
            error = $"unit '{q.Unit}' cannot be converted to canonical '{CanonicalUnit(kind)}'";
            return false;
        }
        result = new Quantity(q.Value * factor, CanonicalUnit(kind), q.Datum);
        return true;
    }
}
