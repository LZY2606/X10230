using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoChron.Core;

/// <summary>深度/长度单位。</summary>
public enum DepthUnit { Cm, M, Ft, In }

/// <summary>年龄时间单位。</summary>
public enum TimeUnit { Yr, Kyr, Myr, Bp1950, CalYrBP }

/// <summary>
/// 带原始表示的领域量。导入层保留 <see cref="Raw"/>；只有派生层才规范化。
/// 无法转换时 <see cref="Canonical"/> 为 null 并保留诊断，绝不丢值。
/// </summary>
public sealed record Quantity(
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("canonical")] double? Canonical,
    [property: JsonPropertyName("canonicalUnit")] string? CanonicalUnit,
    [property: JsonPropertyName("conversionDiagnostic")] string? ConversionDiagnostic = null)
{
    public bool IsConvertible => Canonical.HasValue && ConversionDiagnostic is null;

    public static Quantity Parse(string? raw, string? unit, Func<double, double> toCanonical, string canonicalUnit)
    {
        raw ??= "";
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return new Quantity(raw, unit, null, canonicalUnit, "Q001: 数值为空，不能用默认值替代");
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new Quantity(raw, unit, null, canonicalUnit,
                $"Q002: 无法解析的数值表示 '{trimmed}'，原值保留在原位置");
        if (!double.IsFinite(value))
            return new Quantity(raw, unit, null, canonicalUnit, "Q003: 数值必须有限");
        var c = toCanonical(value);
        if (!double.IsFinite(c))
            return new Quantity(raw, unit, null, canonicalUnit, "Q004: 规范化结果非有限");
        return new Quantity(raw, unit, c, canonicalUnit, null);
    }
}

public static class UnitConversions
{
    public static Quantity Depth(string? raw, string? unit)
    {
        var u = NormalizeDepthUnit(unit);
        if (u is null)
            return new Quantity(raw ?? "", unit, null, "cm", $"Q010: 未知深度单位 '{unit}'，保留原始表示");
        double factor = u switch
        {
            DepthUnit.Cm => 1.0,
            DepthUnit.M => 100.0,
            DepthUnit.Ft => 30.48,
            DepthUnit.In => 2.54,
            _ => double.NaN
        };
        return Quantity.Parse(raw, unit, v => v * factor, "cm");
    }

    public static DepthUnit? NormalizeDepthUnit(string? unit) => (unit ?? "").Trim().ToLowerInvariant() switch
    {
        "cm" => DepthUnit.Cm,
        "m" => DepthUnit.M,
        "ft" or "'" => DepthUnit.Ft,
        "in" or "\"" => DepthUnit.In,
        _ => null
    };

    /// <summary>规范化年龄为 ka（距今，正值表示更老）。BP1950 基准原样换算到 ka。</summary>
    public static Quantity Age(string? raw, string? unit)
    {
        var u = NormalizeTimeUnit(unit);
        if (u is null)
            return new Quantity(raw ?? "", unit, null, "ka", $"Q011: 未知时间单位/基准 '{unit}'，保留原始表示");
        double factor = u switch
        {
            TimeUnit.Yr or TimeUnit.CalYrBP or TimeUnit.Bp1950 => 1.0 / 1000.0,
            TimeUnit.Kyr => 1.0,
            TimeUnit.Myr => 1000.0,
            _ => double.NaN
        };
        return Quantity.Parse(raw, unit, v => v * factor, "ka");
    }

    public static TimeUnit? NormalizeTimeUnit(string? unit) => (unit ?? "").Trim().ToLowerInvariant() switch
    {
        "yr" or "a" or "years" => TimeUnit.Yr,
        "kyr" or "ka" => TimeUnit.Kyr,
        "myr" or "ma" => TimeUnit.Myr,
        "bp1950" or "14c bp" or "radiocarbon bp" => TimeUnit.Bp1950,
        "cal yr bp" or "cal bp" or "cal yr bp (intcal20)" => TimeUnit.CalYrBP,
        _ => null
    };
}

/// <summary>带位置的诊断：输入不完整时返回，禁止用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] string Severity = "error");

public static class StableIds
{
    /// <summary>对规范化 JSON 输入计算稳定标识；等价输入得到相同标识。</summary>
    public static string Hash(string canonical)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string Json(object? value) =>
        JsonSerializer.Serialize(value, CaseJson.Options);
}
