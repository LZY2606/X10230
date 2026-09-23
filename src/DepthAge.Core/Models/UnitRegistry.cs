namespace DepthAge.Core.Models;

/// <summary>
/// 单位换算表：规范化只发生在派生层。无法识别的单位不报错丢弃，
/// 而是留在原位置并附诊断。
/// </summary>
public sealed class UnitRegistry
{
    // 规范化单位：深度 m（沿岩芯向下为正，相对显式深度基准）；年龄 cal yr BP
    private static readonly Dictionary<string, (UnitKind Kind, string Canonical, double Factor)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = (UnitKind.Depth, "m", 1.0),
        ["cm"] = (UnitKind.Depth, "m", 0.01),
        ["mm"] = (UnitKind.Depth, "m", 0.001),
        ["mbsf"] = (UnitKind.Depth, "m", 1.0),
        ["mcd"] = (UnitKind.Depth, "m", 1.0),
        ["yr"] = (UnitKind.Age, "cal yr BP", 1.0),
        ["yr bp"] = (UnitKind.Age, "cal yr BP", 1.0),
        ["cal yr bp"] = (UnitKind.Age, "cal yr BP", 1.0),
        ["cal bp"] = (UnitKind.Age, "cal yr BP", 1.0),
        ["ka"] = (UnitKind.Age, "cal yr BP", 1000.0),
        ["kyr"] = (UnitKind.Age, "cal yr BP", 1000.0),
        ["kyr bp"] = (UnitKind.Age, "cal yr BP", 1000.0),
        ["14c yr bp"] = (UnitKind.Age, "?", 0.0), // 需要校准曲线，不能线性换算
        ["14c yr"] = (UnitKind.Age, "?", 0.0),
        ["bp"] = (UnitKind.Age, "cal yr BP", 1.0),
        ["cm/kyr"] = (UnitKind.Rate, "cm/kyr", 1.0),
        ["m/kyr"] = (UnitKind.Rate, "cm/kyr", 100.0),
        ["mm/yr"] = (UnitKind.Rate, "cm/kyr", 100.0),
        ["cm/yr"] = (UnitKind.Rate, "cm/kyr", 100_000.0)
    };

    public Quantity Parse(string rawText, double? value, string? unit, UnitKind? expectedKind = null, string? datumId = null)
    {
        var quantity = new Quantity
        {
            RawText = rawText,
            Value = value,
            Unit = unit?.Trim(),
            Kind = expectedKind,
            DatumId = datumId
        };

        if (value is null)
        {
            quantity.Converted = false;
            quantity.Diagnostics.Add("数值缺失：输入不完整，未使用默认值代替");
            return quantity;
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            quantity.Converted = false;
            quantity.Diagnostics.Add("单位缺失：无法在不丢失语义的前提下规范化");
            return quantity;
        }

        if (!Units.TryGetValue(unit.Trim(), out var def))
        {
            quantity.Converted = false;
            quantity.Diagnostics.Add($"未知单位 '{unit.Trim()}'：保留原始表示，未参与派生计算");
            return quantity;
        }

        if (expectedKind is { } kind && def.Kind != kind)
        {
            quantity.Converted = false;
            quantity.Diagnostics.Add($"单位量纲不匹配：'{unit}' 是 {def.Kind}，期望 {kind}");
            return quantity;
        }

        if (def.Canonical == "?")
        {
            quantity.Converted = false;
            quantity.Kind = def.Kind;
            quantity.Diagnostics.Add("放射性碳年龄需经校准曲线转换（非线性），不能直接线性规范化");
            return quantity;
        }

        quantity.Kind = def.Kind;
        quantity.CanonicalUnit = def.Canonical;
        quantity.CanonicalValue = value.Value * def.Factor;
        quantity.Converted = true;
        return quantity;
    }
}
