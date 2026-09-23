using System.Globalization;

namespace ChronoCore.Quantities;

/// <summary>垂直坐标基准类型。所有深度比较都必须在同一基准下进行。</summary>
public enum DepthDatumKind
{
    Unspecified = 0,
    LocalMeanSeaLevel = 1,
    LowestAstronomicalTide = 2,
    CoreTop = 3,
    OtherNamed = 4,
}

/// <summary>项目深度基准（不可变材料）。</summary>
public sealed record DepthDatum(
    string Id,
    DepthDatumKind Kind,
    string Name,
    double? ElevationM,
    string ElevationFrame,
    string SourcePath,
    string? Note);

/// <summary>基准之间的连测关系；无法闭合的连测保留诊断而不是猜测偏移。</summary>
public sealed record BenchmarkTie(
    string Id,
    string FromDatumId,
    string ToDatumId,
    double OffsetM,
    bool Closed,
    string SourcePath,
    string? Diagnostic);

/// <summary>原始深度测量：导入时逐字保留原始表示，派生层才规范化为米。</summary>
public sealed record RawDepth
{
    public required string Id { get; init; }
    public required string RawText { get; init; }
    public required string SourcePath { get; init; }
    public string? Unit { get; init; }
    public double? ValueM { get; init; }
    public string? Diagnostic { get; init; }

    public bool IsConvertible => ValueM is not null && Diagnostic is null;

    public static RawDepth Fail(string id, string raw, string source, string diagnostic) =>
        new() { Id = id, RawText = raw, SourcePath = source, Diagnostic = diagnostic };
}

/// <summary>规范化深度（米），携带其基准身份。</summary>
public readonly record struct Depth(double Meters, string DatumId) : IComparable<Depth>
{
    public int CompareTo(Depth other)
    {
        if (DatumId != other.DatumId)
            throw new InvalidOperationException(
                $"拒绝跨基准比较深度：{DatumId} vs {other.DatumId}；请先显式连测。");
        return Meters.CompareTo(other.Meters);
    }

    public override string ToString() =>
        Meters.ToString("0.###", CultureInfo.InvariantCulture) + " m (" + DatumId + ")";
}

/// <summary>深度单位换算；无法识别的值留在原位置并附诊断。</summary>
public static class DepthUnits
{
    private static readonly Dictionary<string, double> Factors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["米"] = 1.0,
        ["cm"] = 0.01, ["centimeter"] = 0.01, ["centimeters"] = 1.0 / 100.0, ["厘米"] = 0.01,
        ["mm"] = 0.001, ["millimeter"] = 0.001, ["毫米"] = 0.001,
        ["ft"] = 0.3048, ["feet"] = 0.3048, ["英尺"] = 0.3048,
    };

    public static bool TryParse(string raw, out double meters, out string? unit, out string? diagnostic)
    {
        meters = 0; unit = null; diagnostic = null;
        var text = raw.Trim();
        var split = SplitValueUnit(text);
        if (split is null)
        {
            diagnostic = $"无法解析深度值「{raw}」：缺少数值或单位";
            return false;
        }
        var (num, u) = split.Value;
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            diagnostic = $"无法解析深度数值「{num}」";
            return false;
        }
        if (!Factors.TryGetValue(u, out var f))
        {
            diagnostic = $"无法转换深度单位「{u}」；原值保留在原位";
            return false;
        }
        meters = v * f;
        unit = u;
        return true;
    }

    private static (string Num, string Unit)? SplitValue(string text)
    {
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ',' or '-' or '+' or 'e' or 'E' or ' '))
            i++;
        if (i == 0 || i >= text.Length) return null;
        return (text[..i].Trim(), text[i..].Trim());
    }
}
