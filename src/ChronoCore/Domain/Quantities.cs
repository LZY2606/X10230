using System.Globalization;
using System.Text.Json.Serialization;

namespace ChronoCore.Domain;

/// <summary>深度坐标基准。所有深度必须声明其基准，派生层规范化为 mbsf。</summary>
public enum DepthDatum { Mbsf = 0, Mblf = 1, CmBelowCoreTop = 2 }

/// <summary>时间基准。年龄派生层规范化为 BP（1950.0）。</summary>
public enum TimeScale { Bp1950 = 0, B2k = 1, KaBp = 2, CalBp = 3 }

public enum BoundaryKind { Continuous = 0, Gap = 1, Unconformity = 2, Fault = 3 }

/// <summary>不可变材料类别（与可修改决定分层）。</summary>
public enum MaterialKind
{
    DepthDatum = 0, SegmentGap = 1, RadiometricDate = 2,
    ReservoirOffset = 3, EventCorrelation = 4, ExclusionDecision = 5
}

public enum DecisionKind { Exclude = 0, Reinstate = 1, ParameterRevision = 2, ManualOverride = 3 }
public enum DecisionStatus { Proposed = 0, Accepted = 1, Rejected = 2, Conflict = 3 }

/// <summary>诊断严重级。</summary>
public enum Severity { Info = 0, Warning = 1, Error = 2 }

/// <summary>
/// 带原始表示的测量量。导入层只做记录；规范化失败时 <see cref="ValueSi"/> 为 null，
/// 值留在 <see cref="Raw"/> 原位置并附带 <see cref="Diagnostic"/>，绝不为画图而丢弃。
/// </summary>
public sealed record Measurement
{
    public double? ValueSi { get; init; }
    public string Unit { get; init; } = "";
    public string Raw { get; init; } = "";
    public string? Diagnostic { get; init; }
    public bool IsConvertible => ValueSi is not null && Diagnostic is null;

    public static Measurement ParseDepth(string? raw, DepthDatum datum)
    {
        var (num, unit, diag) = ParseNumber(raw);
        if (diag is not null) return new Measurement { Raw = raw ?? "", Unit = unit ?? "", Diagnostic = diag };
        double? si = datum switch
        {
            DepthDatum.Mbsf when unit is "m" or "mbsf" or "" => num,
            DepthDatum.Mblf when unit is "m" or "mblf" => num,
            DepthDatum.CmBelowCoreTop when unit is "cm" => num / 100.0,
            DepthDatum.CmBelowCoreTop when unit is "m" => num,
            _ => null
        };
        if (si is null)
            return new Measurement { Raw = raw ?? "", Unit = unit ?? "", Diagnostic = $"深度单位 '{unit}' 与基准 {datum} 不兼容，无法规范化" };
        return new Measurement { ValueSi = si, Unit = unit, Raw = raw ?? "" };
    }

    public static Measurement ParseAge(string? raw, TimeScale scale)
    {
        var (num, unit, diag) = ParseNumber(raw);
        if (diag is not null) return new Measurement { Raw = raw ?? "", Unit = unit ?? "", Diagnostic = diag };
        double? yrBp = (scale, unit) switch
        {
            (TimeScale.Bp1950, "yr BP" or "yr" or "") => num,
            (TimeScale.Bp1950, "ka" or "ka BP") => num * 1000.0,
            (TimeScale.CalBp, "cal yr BP" or "cal yr" or "yr BP" or "yr") => num,
            (TimeScale.KaBp, "ka" or "ka BP") => num * 1000.0,
            (TimeScale.B2k, "yr b2k" or "yr") => num - 50.0,
            _ => null
        };
        if (yrBp is null)
            return new Measurement { Raw = raw ?? "", Unit = unit ?? "", Diagnostic = $"年龄单位 '{unit}' 与时间基准 {scale} 不兼容，无法规范化" };
        return new Measurement { ValueSi = yrBp, Unit = unit, Raw = raw ?? "" };
    }

    public static Measurement ParseOffset(string? raw)
    {
        var (num, unit, diag) = ParseNumber(raw);
        if (diag is not null) return new Measurement { Raw = raw ?? "", Unit = unit ?? "", Diagnostic = diag };
        double? yr = unit switch
        {
            "yr" or "" => num,
            "ka" => num * 1000.0,
            _ => null
        };
        if (yr is null)
            return new Measurement { Raw = raw ?? "", Unit = unit, Diagnostic = $"储层偏移单位 '{unit}' 无法转换为 yr" };
        return new Measurement { ValueSi = yr, Unit = unit, Raw = raw ?? "" };
    }

    internal static (double? num, string unit, string? diag) ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (null, "", "空值：输入不完整，不能以默认值伪装成功");
        var s = raw.Trim();
        var split = System.Array.FindIndex(s.ToCharArray(), c => !(char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E' or ','));
        string numPart = split < 0 ? s : s[..split];
        string unitPart = split < 0 ? "" : s[split..].Trim();
        if (numPart.Length == 0)
            return (null, unitPart, $"无法解析的数值表示（无数值），原始值原位保留");
        if (!double.TryParse(numPart.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return (null, unitPart, $"无法解析的数值表示 '{numPart}'，原始值原位保留");
        if (!double.IsFinite(n))
            return (null, unitPart, $"数值非有限: '{numPart}'");
        return (n, unitPart, null);
    }

    public string FormatSi(string siUnit, int digits = 0) =>
        ValueSi is null ? Raw : Math.Round(ValueSi.Value, digits).ToString("F" + digits, CultureInfo.InvariantCulture) + " " + siUnit;
}

/// <summary>带位置的诊断（片段路径/行列/材料标识）。</summary>
public sealed record DiagnosticEntry
{
    public string Code { get; init; } = "";
    public Severity Severity { get; init; }
    public string Location { get; init; } = "";
    public string Message { get; init; } = "";
    public string? MaterialId { get; init; }
}
