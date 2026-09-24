using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreChrono.Core.Domain;

public enum DepthUnit { Meters, Centimeters, Feet }
public enum AgeUnit { CalYrBP, YrBP, KaBP }

/// <summary>深度值：规范化到米，同时保留原始数值、单位与深度基准（坐标基准）。</summary>
public sealed record DepthValue(double Meters, DepthUnit OriginalUnit, double OriginalValue, string Datum)
{
    public string Display => $"{OriginalValue.ToString(CultureInfo.InvariantCulture)} {DepthUnits.Symbol(OriginalUnit)} @{Datum}";
}

/// <summary>年龄值：规范化到 cal yr BP，同时保留原始表示与时间基准。</summary>
public sealed record AgeValue(double CalYrBP, AgeUnit OriginalUnit, double OriginalValue, string TimeScale)
{
    public string Display => $"{OriginalValue.ToString(CultureInfo.InvariantCulture)} {AgeUnits.Symbol(OriginalUnit)} ({TimeScale})";
}

public static class DepthUnits
{
    public static string Symbol(DepthUnit u) => u switch
    {
        DepthUnit.Meters => "m", DepthUnit.Centimeters => "cm", DepthUnit.Feet => "ft", _ => "?"
    };
    public static double ToMeters(double v, DepthUnit u) => u switch
    {
        DepthUnit.Meters => v, DepthUnit.Centimeters => v / 100.0, DepthUnit.Feet => v * 0.3048, _ => v
    };
}

public static class AgeUnits
{
    public static string Symbol(AgeUnit u) => u switch
    {
        AgeUnit.CalYrBP => "cal yr BP", AgeUnit.YrBP => "yr BP", AgeUnit.KaBP => "ka BP", _ => "?"
    };
    public static double ToCalYrBP(double v, AgeUnit u) => u switch
    {
        AgeUnit.KaBP => v * 1000.0, _ => v
    };
}

/// <summary>解析原始表示（如 "482 cm"、"5.2 ka BP"）。无法转换时返回诊断，值留在原位置。</summary>
public static class QuantityParser
{
    public static bool TryParseDepth(string? raw, string datum, out DepthValue? value, out Diagnostic? diagnostic)
    {
        value = null; diagnostic = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostic = Diagnostic.Error("depth.missing", datum, "深度原始表示为空");
            return false;
        }
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            diagnostic = Diagnostic.Error("depth.unparseable", datum, $"无法解析深度原始表示 '{raw}'");
            return false;
        }
        var unitToken = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "m";
        var unit = unitToken switch
        {
            "m" or "meter" or "meters" => DepthUnit.Meters,
            "cm" or "centimeter" or "centimeters" => DepthUnit.Centimeters,
            "ft" or "feet" or "foot" => DepthUnit.Feet,
            _ => (DepthUnit?)null
        };
        if (unit is null)
        {
            diagnostic = Diagnostic.Error("depth.unknown-unit", datum, $"未知深度单位 '{unitToken}'，原值保留在 '{raw}'");
            return false;
        }
        value = new DepthValue(DepthUnits.ToMeters(v, unit.Value), unit.Value, v, datum);
        return true;
    }

    public static bool TryParseAgeValue(string? raw, string timeScale, out AgeValue? value, out Diagnostic? diagnostic)
    {
        value = null; diagnostic = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostic = Diagnostic.Error("age.missing", timeScale, "年龄原始表示为空");
            return false;
        }
        var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            diagnostic = Diagnostic.Error("age.unparseable", timeScale, $"无法解析年龄原始表示 '{raw}'");
            return false;
        }
        var rest = string.Join(' ', tokens.Skip(1)).ToLowerInvariant();
        var unit = rest switch
        {
            "" or "yr" or "yr bp" or "years" => AgeUnit.YrBP,
            "cal yr bp" or "cal yr" or "calbp" => AgeUnit.CalYrBP,
            "ka bp" or "ka" => AgeUnit.KaBP,
            _ => (AgeUnit?)null
        };
        if (unit is null)
        {
            diagnostic = Diagnostic.Error("age.unknown-unit", timeScale, $"未知年龄单位 '{rest}'，原值保留在 '{raw}'");
            return false;
        }
        value = new AgeValue(AgeUnits.ToCalYrBP(v, unit.Value), unit.Value, v, timeScale);
        return true;
    }
}

/// <summary>确定性随机源：等价输入得到等价抽样与稳定顺序。</summary>
public sealed class DeterministicRandom
{
    private ulong _state;
    public DeterministicRandom(ulong seed) { _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }
    public ulong NextUInt64()
    {
        _state ^= _state << 13; _state ^= _state >> 7; _state ^= _state << 17;
        return _state;
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    public double NextStandardNormal()
    {
        var u1 = Math.Max(NextDouble(), 1e-12);
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

public static class StableIds
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static string ShortHash(string content) => Hash(content)[..12];
    public static string CanonicalJson<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
