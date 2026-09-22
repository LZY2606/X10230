using System.Globalization;
using System.Text.Json.Serialization;

namespace ChronoCore.Domain.Quantities;

/// <summary>深度基准（坐标基准必须显式）。</summary>
public enum DepthDatum { Mbsf = 0, Mbmp = 1, Msl = 2, CmComposite = 3 }

/// <summary>时间基准（年龄单位/纪元）。</summary>
public enum AgeBasis
{
    /// <summary>BP=1950，放射性碳惯用年龄（未做日历校正）。</summary>
    RadiocarbonYearsBP = 0,
    /// <summary>BP=1950 日历年（cal BP）。</summary>
    CalibratedYearsBP = 1,
    /// <summary>距今 Ma（地质长半衰期年代）。</summary>
    MaBeforePresent = 2
}

/// <summary>
/// 带基准与单位的深度。导入时保留原始表示；只有 unit/datum 可识别时才规范化。
/// </summary>
public sealed record Depth
{
    public required string Id { get; init; }
    public required string RawText { get; init; }
    public double? ValueM { get; init; }
    public required DepthDatum Datum { get; init; }
    public required string RawUnit { get; init; }
    public bool Converted => ValueM is not null;

    public static Depth? TryParse(string id, string raw, DepthDatum datum, out string? diagnostic)
    {
        diagnostic = null;
        var trimmed = raw.Trim();
        var unit = DepthUnitOf(trimmed, datum);
        var numPart = ExtractNumber(trimmed);
        if (numPart is null || !double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            diagnostic = $"深度值无法转换：'{raw}' 不是可解析数值，原始表示保留在 {id}";
            return new Depth { Id = id, RawText = raw, Datum = datum, RawUnit = unit ?? RawUnitOf(raw) };
        }
        var factor = UnitFactor(unit);
        if (factor is null)
        {
            diagnostic = $"深度单位无法识别：'{raw}'，原始表示保留在 {id}";
            return new Depth { Id = id, RawText = raw, Datum = datum, RawUnit = unit ?? RawUnitOf(raw) };
        }
        return new Depth
        {
            Id = id,
            RawText = raw,
            ValueM = v * factor.Value,
            Datum = datum,
            RawUnit = unit ?? "m"
        };
    }

    private static string? ExtractNumber(string raw)
    {
        var span = raw.AsSpan();
        int start = -1, end = 0;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            bool isNum = char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E';
            if (isNum) { if (start < 0) start = i; end = i + 1; }
            else if (start >= 0) break;
        }
        return start < 0 ? null : span[start..end].ToString();
    }

    private static string? DepthUnitOf(string raw, DepthDatum datum)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("mbsf")) return "mbsf";
        if (lower.Contains("mbmp")) return "mbmp";
        if (lower.Contains("cm")) return "cm";
        if (lower.Contains("m") && char.IsDigit(lower.FirstOrDefault(char.IsDigit, 'x'))) return "m";
        return datum switch
        {
            DepthDatum.CmComposite => "cm",
            _ => "m"
        };
    }

    private static string RawUnitOf(string raw)
    {
        var num = ExtractNumber(raw);
        return num is null ? raw : raw.Replace(num, "", StringComparison.Ordinal).Trim();
    }

    private static double? UnitFactor(string? unit) => unit?.ToLowerInvariant() switch
    {
        "m" or "mbsf" or "mbmp" or "msl" => 1.0,
        "cm" => 0.01,
        "mm" => 0.001,
        _ => null
    };
}

/// <summary>年龄：原始表示、测量年龄与归一化年龄（BP=1950 年）。</summary>
public sealed record Age
{
    public required string Id { get; init; }
    public required string RawText { get; init; }
    /// <summary>测量年龄数值（按原始单位：^14C yr BP、cal yr BP、Ma）。</summary>
    public double? MeasuredValue { get; init; }
    public required AgeBasis Basis { get; init; }
    public required string RawUnit { get; init; }
    /// <summary>归一化：BP=1950 年；无法转换时为 null 并保留诊断。</summary>
    public double? YearsBp { get; init; }
    public string? Diagnostic { get; init; }
    public bool Converted => YearsBp is not null;

    public static Age Parse(string id, string raw, AgeBasis basis, string? rawUnit = null)
    {
        var trimmed = raw.Trim();
        var num = ExtractNumber(trimmed);
        var unit = rawUnit ?? InferUnit(trimmed, basis);
        if (num is null || !double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new Age
            {
                Id = id, RawText = raw, Basis = basis, RawUnit = unit,
                Diagnostic = $"年龄无法转换：'{raw}' 不是可解析数值，原始表示保留在 {id}"
            };
        double? years = basis switch
        {
            AgeBasis.RadiocarbonYearsBP or AgeBasis.CalibratedYearsBP => v,
            AgeBasis.MaBeforePresent => v * 1_000_000.0,
            _ => null
        };
        return new Age { Id = id, RawText = raw, MeasuredValue = v, Basis = basis, RawUnit = unit, YearsBp = years };
    }

    private static string? ExtractNumber(string raw)
    {
        var span = raw.AsSpan();
        int start = -1, end = 0;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            bool isNum = char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E';
            if (isNum) { if (start < 0) start = i; end = i + 1; }
            else if (start >= 0) break;
        }
        return start < 0 ? null : span[start..end].ToString();
    }

    private static string InferUnit(string raw, AgeBasis basis)
    {
        var l = raw.ToLowerInvariant();
        if (l.Contains("ma")) return "Ma";
        if (l.Contains("cal")) return "cal yr BP";
        return basis switch
        {
            AgeBasis.MaBeforePresent => "Ma",
            AgeBasis.CalibratedYearsBP => "cal yr BP",
            _ => "14C yr BP"
        };
    }
}

/// <summary>沉积速率（cm/yr）。内部归一化单位。</summary>
public readonly record struct SedRate(double CmPerYear)
{
    public double MPerKa => CmPerYear * 10.0;
    public static SedRate FromMetersPerYear(double mYr) => new(mYr * 100.0);
    public override string ToString() => $"{CmPerYear:G4} cm/yr";
}
