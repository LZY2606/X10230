using System.Globalization;

namespace StratAge.Core.Domain;

/// <summary>
/// 领域量：所有测量值都携带单位或坐标/时间基准。
/// 原始表示（RawText）在导入时原样保留；只有派生层才做规范化。
/// </summary>
public sealed record MeasuredValue
{
    public required string RawText { get; init; }
    public double? Normalized { get; init; }
    public string? CanonicalUnit { get; init; }
    public string? GivenUnit { get; init; }
    public bool Convertible => Normalized.HasValue;

    public static MeasuredValue Parse(string raw, string dimension, string? givenUnit)
    {
        var (canonicalUnit, factor) = UnitTable.Resolve(dimension, givenUnit);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new MeasuredValue { RawText = raw, GivenUnit = givenUnit };
        if (canonicalUnit is null)
            return new MeasuredValue { RawText = raw, GivenUnit = givenUnit };
        return new MeasuredValue
        {
            RawText = raw,
            GivenUnit = givenUnit,
            CanonicalUnit = canonicalUnit,
            Normalized = v * factor
        };
    }
}

public static class UnitTable
{
    // 规范单位：长度 cm（相对深度基准），年龄 ka BP（BP=1950），速率 ka/cm。
    private static readonly Dictionary<(string Dim, string Unit), (string Canonical, double Factor)> Table = new()
    {
        [("length", "cm")] = ("cm", 1),
        [("length", "m")] = ("cm", 100),
        [("length", "mm")] = ("cm", 0.1),
        [("length", "mbsf")] = ("cm", 100),
        [("age", "ka bp")] = ("ka BP", 1),
        [("age", "ka")] = ("ka BP", 1),
        [("age", "ka b2k")] = ("ka BP", 1.0), // B2K=2000 与 BP=1950 差 50 年，案例中显式给出 datum 处理
        [("age", "cal ka bp")] = ("ka BP", 1),
        [("age", "14c yr bp")] = ("ka BP", 1d / 1000d), // 放射性碳原始年龄：标记后由派生层校准
        [("age", "yr bp")] = ("ka BP", 1d / 1000d),
        [("age", "a bp")] = ("ka BP", 1d / 1000d),
        [("rate", "cm/ka")] = ("cm/ka", 1),
        [("rate", "ka/cm")] = ("cm/ka", 0),   // ka/cm 为倒数，0 表示需要派生转换
        [("offset", "14c yr")] = ("14C yr", 1),
        [("offset", "yr")] = ("14C yr", 1),
    };

    public static (string? Canonical, double Factor) Resolve(string dimension, string? unit)
    {
        var key = unit?.Trim().ToLowerInvariant();
        if (key is null) return (null, 1);
        if (Table.TryGetValue((dimension, key), out var hit)) return (hit.Canonical, hit.Factor);
        return (null, 1);
    }

    public static bool IsKnownUnit(string dimension, string? unit) => Resolve(dimension, unit).Canonical is not null;
}

/// <summary>深度基准。坐标基准属于不可变材料的一部分。</summary>
public sealed record DepthDatum
{
    public required string Id { get; init; }          // dat-…
    public required string Label { get; init; }
    public required string Direction { get; init; }   // "down-positive"
    public required string CanonicalUnit { get; init; } // cm
    public string? Note { get; init; }
}

/// <summary>时间基准（BP=1950 / B2K=2000 / …）。</summary>
public sealed record TimeDatum
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Epoch { get; init; }       // BP1950 / B2K2000
    public double ShiftToBpKa { get; init; }          // 加到 ka 值上换算到 BP1950（ka）
}
