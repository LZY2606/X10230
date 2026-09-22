using System.Collections.Generic;

namespace GSB.Core.Quantities;

/// <summary>
/// 单位换算表：规范化单位为 cm（长度）、cal yr BP（年龄）、cm/kyr（速率）。
/// 放射性碳 14C 年龄不是“另一单位的 cal BP”——它需要校准曲线，
/// 因此 14C yr 在这里显式不可线性换算，由校准分布处理。
/// </summary>
public static class UnitTable
{
    // factor * value(原单位) = value(规范单位)
    private static readonly Dictionary<string, (double Factor, string Canonical)> Length = new()
    {
        ["cm"] = (1.0, "cm"),
        ["m"] = (100.0, "cm"),
        ["mm"] = (0.1, "cm"),
        ["mbsf"] = (100.0, "cm"), // 米为单位的钻深
        ["cmbsf"] = (1.0, "cm"),
    };

    private static readonly Dictionary<string, (double Factor, string Canonical)> Age = new()
    {
        ["cal yr BP"] = (1.0, "cal yr BP"),
        ["cal BP"] = (1.0, "cal yr BP"),
        ["cal ka BP"] = (1000.0, "cal yr BP"),
        ["yr BP"] = (1.0, "cal yr BP"),
        ["BC/AD"] = (1.0, "cal yr BP"), // 需额外时代标注；导入层决定如何处理
        // 14C age（如 "14C yr BP"）故意缺失：必须经过校准，不得线性换算。
    };

    private static readonly Dictionary<string, (double Factor, string Canonical)> Rate = new()
    {
        ["cm/kyr"] = (1.0, "cm/kyr"),
        ["cm/yr"] = (1000.0, "cm/kyr"),
        ["m/Myr"] = (0.1, "cm/kyr"),
        ["mm/yr"] = (100.0, "cm/kyr"),
    };

    public static bool TryGetFactor(QuantityKind kind, string unit, out double factor, out string canonical)
    {
        var table = kind switch
        {
            QuantityKind.Length => Length,
            QuantityKind.Age => Age,
            QuantityKind.Rate => Rate,
            _ => null
        };
        canonical = kind switch
        {
            QuantityKind.Length => "cm",
            QuantityKind.Age => "cal yr BP",
            QuantityKind.Rate => "cm/kyr",
            _ => "?"
        };
        factor = 0;
        if (table is not null && table.TryGetValue(unit.Trim(), out var entry))
        {
            factor = entry.Factor;
            canonical = entry.Canonical;
            return true;
        }
        return false;
    }
}
