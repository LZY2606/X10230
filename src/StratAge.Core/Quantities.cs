namespace StratAge.Core;

/// <summary>诊断条目：永远携带机器可读代码；可定位时携带路径与行列。</summary>
public sealed record Diag(string Code, string Message, string? Path = null, int? Line = null, int? Column = null, string? TargetId = null, string Severity = "error")
{
    public Diag At(string targetId) => this with { TargetId = targetId };
}

/// <summary>规范化后的量（派生层使用）；原始表示永不被覆盖。</summary>
public sealed record CanonicalQty(double Value, string Unit, string Basis);

/// <summary>
/// 领域量：原始文本表示必须保留；Value/Unit/Basis 是照录的结构化片段，
/// Canonical 仅在派生层可转换时生成。无法转换的值留在原位并附诊断。
/// </summary>
public sealed record Quantity
{
    public required string Raw { get; init; }
    public double? Value { get; init; }
    public required string Unit { get; init; }
    public string? Basis { get; init; }
    public CanonicalQty? Canonical { get; init; }
    public string Status { get; init; } = "ok"; // ok | unconvertible
    public IReadOnlyList<Diag> Diagnostics { get; init; } = [];

    public double RequireCanonical() => Canonical?.Value
        ?? throw new InvalidOperationException($"quantity has no canonical value: {Raw} ({Status})");
}

/// <summary>单位/坐标基准注册表。仅收录沉积年代尺场景明确的转换。</summary>
public static class UnitTable
{
    // 长度 -> m（米）
    static readonly Dictionary<string, (double factor, string basis)> Length = new()
    {
        ["m"] = (1.0, "m"), ["cm"] = (0.01, "m"), ["mm"] = (0.001, "m"),
        ["mbsf"] = (1.0, "mbsf"), ["mcd"] = (1.0, "mcd"),
        ["cmbsf"] = (0.01, "mbsf"),
    };

    // 时间：同基准内的尺度换算；放射性碳年不允许在无校准版本时直接当日历年
    static readonly Dictionary<string, (double factor, string basis)> Time = new()
    {
        ["yr bp"] = (1.0, "BP1950"),
        ["cal bp"] = (1.0, "BP1950"),
        ["yr"] = (1.0, "yr"),
        ["ka bp"] = (1000.0, "BP1950"),
        ["ka"] = (1000.0, "yr"),
    };

    public static bool IsLengthUnit(string unit) => Length.ContainsKey(unit.Trim().ToLowerInvariant());
    public static bool IsTimeUnit(string unit) => Time.ContainsKey(unit.Trim().ToLowerInvariant());

    public static CanonicalQty? ConvertLength(double value, string unit, string? datumBasis, List<Diag> diags, string? path = null)
    {
        var key = unit.Trim().ToLowerInvariant();
        if (!Length.TryGetValue(key, out var m))
        {
            diags.Add(new Diag("UNIT_UNKNOWN", $"未知长度单位 '{unit}'，值保留在原位置，不参与绘图", path));
            return null;
        }
        var basis = string.IsNullOrEmpty(datumBasis) ? m.basis : datumBasis!;
        return new CanonicalQty(value * m.factor, m.basis is "mbsf" or "mcd" ? "m" : "m", basis);
    }

    public static CanonicalQty? ConvertTime(double value, string unit, List<Diag> diags, string? path = null)
    {
        var key = unit.Trim().ToLowerInvariant();
        if (key is "14c yr bp" or "14c bp" or "radiocarbon yr bp")
        {
            diags.Add(new Diag("AGE_NEEDS_CALIBRATION",
                "放射性碳年龄必须经储层扣除与校准版本派生为日历年，原始分布保留，不能直接当日历年", path, Severity: "warn"));
            return null;
        }
        if (!Time.TryGetValue(key, out var m))
        {
            diags.Add(new Diag("UNIT_UNKNOWN", $"未知时间单位 '{unit}'，值保留在原位置，不参与绘图", path));
            return null;
        }
        return new CanonicalQty(value * m.factor, "yr BP", m.basis);
    }

    public static bool IsRadiocarbon(string unit) =>
        unit.Trim().ToLowerInvariant() is "14c yr bp" or "14c bp" or "radiocarbon yr bp";
}
