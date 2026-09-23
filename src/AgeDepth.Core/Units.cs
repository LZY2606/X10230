using System.Globalization;
using System.Text.Json;

namespace AgeDepth.Core;

/// <summary>物理量维度。所有领域量必须携带单位与坐标/时间基准。</summary>
public enum Dimension { Depth, Age, Rate }

/// <summary>规范化后的物理量：SI 值 + 规范单位 + 基准。</summary>
public sealed record Quantity(double Value, string Unit, string Datum)
{
    public override string ToString() => $"{Value.ToString("G6", CultureInfo.InvariantCulture)} {Unit} @{Datum}";
}

/// <summary>带位置的诊断。位置指向来源与 JSON 路径，不允许用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(string Code, string Severity, string Position, string Message)
{
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JsonOpts.Default);
}

/// <summary>原始表示：导入时原样保留，派生层才做规范化。</summary>
public sealed record RawQuantity(string Raw, string Unit, string Datum);

public static class UnitConverter
{
    // 深度：规范化为 m，基准为岩芯顶（core-top）
    private static readonly Dictionary<string, double> DepthFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048
    };
    // 年龄：规范化为 yr BP，基准 BP1950
    private static readonly Dictionary<string, double> AgeFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yr BP"] = 1.0, ["a BP"] = 1.0, ["cal yr BP"] = 1.0, ["ka BP"] = 1000.0, ["Ma"] = 1_000_000.0
    };

    public const string DepthCanonicalUnit = "m";
    public const string AgeCanonicalUnit = "yr BP";
    public const string AgeDatum = "BP1950";

    public static bool TryNormalize(double value, string unit, Dimension dim, string datum, out Quantity q, out Diagnostic? diag)
    {
        q = default!;
        diag = null;
        var table = dim switch
        {
            Dimension.Depth => DepthFactors,
            Dimension.Age => AgeFactors,
            _ => null
        };
        if (table is null || !table.TryGetValue(unit ?? "", out var factor))
        {
            // 无法转换：值留在原位置，附诊断；绝不丢弃或默认化
            diag = new Diagnostic("unit.unconvertible", "error", "",
                $"无法转换单位 '{unit}'（维度 {dim}）；原始值 {value.ToString(CultureInfo.InvariantCulture)} {unit} 保留在原位置");
            return false;
        }
        var canonicalUnit = dim == Dimension.Depth ? DepthCanonicalUnit : AgeCanonicalUnit;
        q = new Quantity(value * factor, canonicalUnit, datum);
        return true;
    }

    public static string DepthDatum(string coreId) => $"core-top:{coreId}";
}
