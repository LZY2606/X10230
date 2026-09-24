using System.Globalization;
using System.Text.Json.Serialization;

namespace Chrono.Core;

public enum MeasureKind { Length, Age, Rate, Offset, Dimensionless }

/// <summary>
/// A unit definition. All internal normalization uses the SI-ish canonical unit:
/// length -> metres, age -> years (cal BP when the datum says so), rate -> m/ka.
/// </summary>
public sealed record UnitDef(string Id, MeasureKind Kind, double ToCanonical, string Symbol, string Name);

public static class Units
{
    public static readonly UnitDef Meters = new("m", MeasureKind.Length, 1.0, "m", "metres");
    public static readonly UnitDef Centimeters = new("cm", MeasureKind.Length, 0.01, "cm", "centimetres");
    public static readonly UnitDef CalBP = new("cal_BP", MeasureKind.Age, 1.0, "cal BP", "calendar years before 1950");
    public static readonly UnitDef BP = new("BP", MeasureKind.Age, 1.0, "BP", "uncalibrated radiocarbon years (kept raw)");
    public static readonly UnitDef CalBCE = new("cal_BCE", MeasureKind.Age, 1.0, "cal BCE yr", "calendar BCE years (datum offset needed)");
    public static readonly UnitDef Years = new("yr", MeasureKind.Offset, 1.0, "yr", "years");
    public static readonly UnitDef MPerKa = new("m/ka", MeasureKind.Rate, 1.0, "m/ka", "metres per thousand years");
    public static readonly UnitDef CmPerKa = new("cm/ka", MeasureKind.Rate, 0.01, "cm/ka", "centimetres per thousand years");
    public static readonly UnitDef One = new("1", MeasureKind.Dimensionless, 1.0, "", "dimensionless");

    public static readonly IReadOnlyDictionary<string, UnitDef> ById =
        new[] { Meters, Centimeters, CalBP, BP, CalBCE, Years, MPerKa, CmPerKa, One }
            .ToDictionary(u => u.Id, StringComparer.Ordinal);

    public static UnitDef? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null :
        ById.TryGetValue(id.Trim(), out var u) ? u : null;
}

/// <summary>
/// A measured value that keeps the original representation forever. The derived layer may add a
/// canonical normalized value; unconvertible values stay in place with diagnostics and never get
/// dropped just because a chart needs a number.
/// </summary>
public sealed record RawQuantity
{
    public string Raw { get; init; } = "";
    public string? UnitId { get; init; }
    public double? Normalized { get; init; }
    public string? CanonicalUnitId { get; init; }
    public string? UnconvertedReason { get; init; }

    public bool IsConverted => Normalized is not null && UnconvertedReason is null;

    public static RawQuantity Parse(string raw, MeasureKind expected, string? unitId, double datumShift = 0.0)
    {
        var r = (raw ?? string.Empty).Trim();
        var unit = Units.Find(unitId);
        var result = new RawQuantity { Raw = r, UnitId = unit?.Id ?? unitId };

        if (!double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return result with { UnconvertedReason = $"unparseable numeric text '{r}'" };

        if (unit is null)
            return result with { UnconvertedReason = $"unknown or missing unit '{unitId}' for {expected}" };

        if (unit.Kind != expected)
        {
            // radiocarbon BP vs cal BP look numerically compatible but are not the same datum;
            // we refuse silent conversion and keep the raw value.
            return result with { UnconvertedReason = $"unit '{unitId}' is {unit.Kind}, expected {expected}" };
        }

        return result with
        {
            Normalized = v * unit.ToCanonical + datumShift,
            CanonicalUnitId = CanonicalFor(expected)
        };
    }

    public static string CanonicalFor(MeasureKind kind) => kind switch
    {
        MeasureKind.Length => "m",
        MeasureKind.Age => "cal_BP",
        MeasureKind.Rate => "m/ka",
        MeasureKind.Offset => "yr",
        _ => "1"
    };
}
