using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>坐标/单位基准。所有领域量必须携带单位与基准。</summary>
public enum QuantityKind { Depth, Age, Rate, Offset }

/// <summary>原始表示 + 规范化结果。导入时保留原始表示，派生层才做规范化。</summary>
public sealed record Quantity
{
    public required string Raw { get; init; }          // 原始表示，例如 "12.5 cm"、"1.2 ka BP"
    public required string Unit { get; init; }         // 原始单位，例如 "cm"、"kaBP"
    public required string Datum { get; init; }        // 坐标/时间基准，例如 "mbsf"、"calBP-1950"
    public double? CanonicalValue { get; init; }       // 规范化值；无法转换时为 null（留在原位置并附诊断）
    public string CanonicalUnit { get; init; } = "";   // 规范化单位

    [JsonIgnore] public bool IsConvertible => CanonicalValue.HasValue;
}

public static class UnitConverter
{
    // 深度规范化为 m（mbsf 基准不变），年龄规范化为 cal yr BP。
    public static Quantity Normalize(string raw, string unit, string datum, QuantityKind kind)
    {
        var canonicalUnit = kind switch
        {
            QuantityKind.Depth => "m",
            QuantityKind.Age => "calBP",
            QuantityKind.Rate => "m/kyr",
            QuantityKind.Offset => "yr",
            _ => ""
        };
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return new Quantity { Raw = raw, Unit = unit, Datum = datum, CanonicalValue = null, CanonicalUnit = canonicalUnit };
        }
        double? canonical = (kind, unit.ToLowerInvariant()) switch
        {
            (QuantityKind.Depth, "m") => value,
            (QuantityKind.Depth, "cm") => value / 100.0,
            (QuantityKind.Depth, "mm") => value / 1000.0,
            (QuantityKind.Depth, "ft") => value * 0.3048,
            (QuantityKind.Age, "calbp") => value,
            (QuantityKind.Age, "yrbp") => value,
            (QuantityKind.Age, "kabp") => value * 1000.0,
            (QuantityKind.Age, "14cbp") => value, // 未校正的放射性碳年龄：保留单位语义，由储层偏移校正
            (QuantityKind.Offset, "yr") => value,
            (QuantityKind.Rate, "m/kyr") => value,
            (QuantityKind.Rate, "cm/yr") => value, // 1 cm/yr == 1 m/kyr
            _ => null
        };
        return new Quantity { Raw = raw, Unit = unit, Datum = datum, CanonicalValue = canonical, CanonicalUnit = canonicalUnit };
    }
}
