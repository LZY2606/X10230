using System.Globalization;
using System.Text.RegularExpressions;

namespace AgeDepth.Core;

/// <summary>结构化诊断：带位置，绝不静默吞掉。</summary>
public sealed record Diagnostic(string Code, string Position, string Message, string Severity = "error");

public enum QuantityKind { Depth, Age, Duration }

/// <summary>
/// 原始表示 + 派生规范化值。导入时保留 Raw/Unit/Datum；
/// 无法转换时 Canonical 为 null 且附 Issue，原值留在原位置。
/// </summary>
public sealed record RawQuantity
{
    public required string Raw { get; init; }
    public string? Unit { get; init; }
    public string? Datum { get; init; }
    public double? Canonical { get; init; }
    public string? CanonicalUnit { get; init; }
    public Diagnostic? Issue { get; init; }

    public static RawQuantity Create(string raw, string? unit, string? datum, QuantityKind kind, string position)
    {
        var q = new RawQuantity { Raw = raw, Unit = unit, Datum = datum };
        if (UnitRegistry.TryNormalize(raw, unit, kind, out var canonical, out var canonUnit, out var error))
            return q with { Canonical = canonical, CanonicalUnit = canonUnit };
        return q with { Issue = new Diagnostic("UNIT_UNCONVERTIBLE", position, error!) };
    }

    public string Display =>
        Canonical is null ? $"{Raw} {Unit ?? ""} [{Datum ?? "?"}] (未规范化)".Trim()
                          : $"{Raw} {Unit ?? ""} [{Datum ?? "?"}] = {Canonical:0.###} {CanonicalUnit}".Trim();
}

public static class UnitRegistry
{
    private static readonly Regex NumberPrefix =
        new(@"^\s*([-+]?\d+(?:[.,]\d+)?)\s*([a-zA-Zµ ]*)\s*$", RegexOptions.Compiled);

    public static bool TryNormalize(string raw, string? unit, QuantityKind kind,
        out double canonical, out string canonicalUnit, out string? error)
    {
        canonical = 0; canonicalUnit = CanonicalUnit(kind); error = null;
        var m = NumberPrefix.Match(raw ?? "");
        if (!m.Success)
        {
            error = $"无法解析数值: '{raw}'";
            return false;
        }
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
        {
            error = $"无法解析数值: '{raw}'";
            return false;
        }
        var u = (unit ?? "").Trim();
        if (u.Length == 0) u = m.Groups[2].Value.Trim();
        if (u.Length == 0)
        {
            // 无单位：按该量纲的规范单位对待，但保留原始表示。
            canonical = value;
            return true;
        }
        var factor = kind switch
        {
            QuantityKind.Depth => u switch
            {
                "m" => 1.0, "cm" => 0.01, "mm" => 0.001, "ft" => 0.3048,
                _ => double.NaN
            },
            QuantityKind.Age or QuantityKind.Duration => u switch
            {
                "ka" => 1.0, "Ma" => 1000.0, "yr" => 0.001, "yr BP" => 0.001, "a" => 0.001,
                _ => double.NaN
            },
            _ => double.NaN
        };
        if (double.IsNaN(factor))
        {
            error = $"未知单位 '{u}'（量纲 {kind}）";
            return false;
        }
        canonical = value * factor;
        return true;
    }

    public static string CanonicalUnit(QuantityKind kind) => kind switch
    {
        QuantityKind.Depth => "m",
        QuantityKind.Age => "ka",
        QuantityKind.Duration => "ka",
        _ => "?"
    };
}
