namespace AgeDepthLab.Core;

/// <summary>带单位与坐标/时间基准的量。导入时保留原始表示，派生层才做规范化。</summary>
public sealed record Quantity(double Value, string Unit, string Datum)
{
    public string Raw => $"{Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} {Unit} [{Datum}]";
}

public sealed record ConversionOutcome(Quantity? Normalized, RunDiagnostic? Diagnostic)
{
    public bool Ok => Normalized != null;
}

/// <summary>
/// 单位换算。无法转换的值返回诊断，调用方必须把原值留在原位置，不得丢弃或替换成默认值。
/// </summary>
public static class UnitConverter
{
    // 深度规范化为 m，基准保留声明值
    private static readonly Dictionary<string, double> DepthToMeter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048
    };

    // 年龄规范化为 yr（年），基准保留声明值（如 BP1950 / calBP / ka）
    private static readonly Dictionary<string, double> AgeToYear = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yr"] = 1.0, ["a"] = 1.0, ["ka"] = 1000.0, ["Ma"] = 1_000_000.0
    };

    public static ConversionOutcome Depth(Quantity q, string subjectId)
    {
        if (DepthToMeter.TryGetValue(q.Unit, out var f))
            return new ConversionOutcome(q with { Value = q.Value * f, Unit = "m" }, null);
        return new ConversionOutcome(null, new RunDiagnostic(
            "UNIT_UNCONVERTIBLE", "error",
            $"深度单位 '{q.Unit}' 无法换算为 m，原值保留在原位置", subjectId, q.Value, q.Datum));
    }

    public static ConversionOutcome Age(Quantity q, string subjectId)
    {
        if (AgeToYear.TryGetValue(q.Unit, out var f))
            return new ConversionOutcome(q with { Value = q.Value * f, Unit = "yr" }, null);
        return new ConversionOutcome(null, new RunDiagnostic(
            "UNIT_UNCONVERTIBLE", "error",
            $"年龄单位 '{q.Unit}' 无法换算为 yr，原值保留在原位置", subjectId, q.Value, q.Datum));
    }

    public static ConversionOutcome OffsetYears(Quantity q, string subjectId)
    {
        if (AgeToYear.TryGetValue(q.Unit, out var f))
            return new ConversionOutcome(q with { Value = q.Value * f, Unit = "yr" }, null);
        return new ConversionOutcome(null, new RunDiagnostic(
            "UNIT_UNCONVERTIBLE", "error",
            $"储层偏移单位 '{q.Unit}' 无法换算为 yr，原值保留在原位置", subjectId, null, q.Datum));
    }
}
