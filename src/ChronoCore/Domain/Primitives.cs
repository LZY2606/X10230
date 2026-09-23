using System.Globalization;

namespace ChronoCore.Domain;

/// <summary>带单位与原始表示的测量值。派生层才做规范化，原始表示始终保留。</summary>
public sealed record Measure(double ValueSi, string Unit, string Raw, double? SiValueHint = null)
{
    public double Value => ValueSi;

    public static Measure From(double value, string unit, string? raw = null)
    {
        var (si, normUnit) = ConvertToSi(value, unit);
        return new Measure(si, normUnit, raw ?? FormatRaw(value, unit));
    }

    public static (bool ok, Measure? measure, string? reason) TryParse(string raw)
    {
        var s = raw.Trim();
        var split = FirstUnitBoundary(s);
        if (!double.TryParse(s[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (false, null, $"无法解析数值: '{s}'");
        var unit = s[split..].Trim();
        if (unit.Length == 0)
            return (false, null, "缺少单位");
        try { return (true, From(v, unit, raw), null); }
        catch (NotSupportedException ex) { return (false, null, ex.Message); }
    }

    private static int FirstUnitBoundary(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (char.IsLetter(s[i]) || s[i] == '‰') return i;
        return s.Length;
    }

    private static string FormatRaw(double v, string unit) =>
        v.ToString("0.###", CultureInfo.InvariantCulture) + unit;

    public static (double si, string unit) ConvertToSi(double v, string unit) => unit.Trim() switch
    {
        "m" or "meter" or "meters" => (v, "m"),
        "cm" => (v / 100.0, "m"),
        "mm" => (v / 1000.0, "m"),
        "ka BP" or "ka" or "kyr BP" or "ka cal BP" or "cal ka BP" => (v * 1000.0, "a BP"),
        "a BP" or "yr BP" or "years BP" or "cal a BP" => (v, "a BP"),
        "cm/ka" => (v / 100.0, "m/a"),
        "m/ka" => (v / 1000.0, "m/a"),
        "14C a BP" or "C14yr" => (v, "14C a BP"),
        _ => throw new NotSupportedException($"不支持的单位: '{unit}'（原值保留在原始位置）")
    };
}

/// <summary>时间基准（a BP 以 1950 CE 为零年）。</summary>
public sealed record TimeDatum(string Kind, string Epoch, string Raw)
{
    public static TimeDatum BeforePresent(string? raw = null) =>
        new("BP", "1950-01-01", raw ?? "a BP (zero=1950 CE)");
}

/// <summary>深度基准：深度从何处、向何方向度量。</summary>
public sealed record DepthDatum(string Id, string Name, string Reference, bool PositiveDown, string Raw)
{
    public string StableId => Id;
}

/// <summary>深度坐标 = 规范化米数 + 基准身份。</summary>
public readonly record struct DepthPoint(double Meters, string DatumId) : IComparable<DepthPoint>
{
    public int CompareTo(DepthPoint other)
    {
        var c = string.CompareOrdinal(DatumId, other.DatumId);
        return c != 0 ? c : Meters.CompareTo(other.Meters);
    }
    public static bool operator <(DepthPoint a, DepthPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(DepthPoint a, DepthPoint b) => a.CompareTo(b) > 0;
}
