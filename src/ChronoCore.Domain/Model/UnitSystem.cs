using System.Globalization;

namespace ChronoCore.Domain.Model;

/// <summary>Canonical units for the derived layer: cm below datum, cal yr BP.
/// Imported representations are untouched; only derived computation converts.</summary>
public sealed class UnitSystem
{
    public string CanonicalDepthUnit { get; init; } = "cm";
    public string CanonicalAgeUnit { get; init; } = "cal yr BP";

    public static bool IsDepthConvertible(DepthRef d) => d.Unit switch
    {
        "cm" or "m" or "mm" => true,
        _ => false
    };

    public static double ToCm(DepthRef d) => d.Unit switch
    {
        "cm" => d.Value,
        "m" => d.Value * 100.0,
        "mm" => d.Value / 10.0,
        _ => throw new InvalidOperationException($"深度单位不可转换：{d.Unit}（原始值 {d.Raw} 保留）")
    };

    public static double SharedToCm(DepthRef d) => ToCm(d);

    public static bool IsAgeConvertible(string unit) =>
        unit is "cal yr BP" or "yr BP" or "a BP" or "cal BP" or "yr";

    /// <summary>Convert an age value to cal yr BP. ka is scaled; non-cal 14C
    /// units are NOT silently treated as cal (that is a reservoir/calibration
    /// decision) and fail conversion, leaving the raw value in place.</summary>
    public static double ToCalYearBP(double value, string unit) => unit switch
    {
        "cal yr BP" or "yr BP" or "a BP" or "cal BP" or "yr" => value,
        "ka BP" or "ka" => value * 1000.0,
        _ => throw new InvalidOperationException($"年龄单位不可规范化：{unit}（需显式校正/储层决定）")
    };

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
