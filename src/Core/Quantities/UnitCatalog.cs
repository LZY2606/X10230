using System.Globalization;

namespace AgeModel.Core.Quantities;

/// <summary>
/// Dimensioned unit catalogue. Conversion to a canonical SI-like unit is allowed only
/// for units of the same dimension with a known linear factor. Unknown/unconvertible
/// units must be retained verbatim by callers (see <see cref="RawQuantity"/>).
/// </summary>
public sealed record UnitDef(string Id, string Dimension, double ToCanonicalFactor, string Symbol, IReadOnlyList<string> Aliases);

public sealed class UnitCatalog
{
    public const string Length = "length";
    public const string Time = "time";
    public const string Rate = "rate";

    private readonly Dictionary<string, UnitDef> _byId;
    private readonly Dictionary<string, UnitDef> _byAlias;

    public UnitCatalog(IEnumerable<UnitDef>? extra = null)
    {
        var defs = new List<UnitDef>
        {
            new("m", Length, 1.0, "m", new[] { "meter", "metre", "meters", "metres" }),
            new("cm", Length, 0.01, "cm", new[] { "centimetre", "centimeter" }),
            new("mm", Length, 0.001, "mm", new[] { "millimetre", "millimeter" }),
            new("a", Time, 1.0, "a", new[] { "yr", "year", "years", "yrs", "annum" }),
            new("ka", Time, 1000.0, "ka", new[] { "kyr", "ky", "thousand-years" }),
            new("m/ka", Rate, 1.0, "m ka⁻¹", Array.Empty<string>()),
            new("cm/ka", Rate, 0.01, "cm ka⁻¹", Array.Empty<string>()),
        };
        if (extra is not null) defs.AddRange(extra);
        _byId = defs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _byAlias = new Dictionary<string, UnitDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs)
        {
            _byAlias[d.Id] = d;
            foreach (var a in d.Aliases) _byAlias[a] = d;
        }
    }

    public UnitDef? Resolve(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        var key = unit.Trim();
        return _byId.TryGetValue(key, out var d) ? d : _byAlias.TryGetValue(key, out var a) ? a : null;
    }

    public UnitDef Require(string id) =>
        _byId.TryGetValue(id, out var d) ? d : throw new KeyNotFoundException($"unknown unit {id}");

    public bool TryConvert(double value, string? fromUnit, string canonicalUnit, out double converted, out string? reason)
    {
        converted = 0;
        reason = null;
        var from = Resolve(fromUnit);
        var to = Resolve(canonicalUnit);
        if (from is null) { reason = $"UNIT_UNKNOWN:{fromUnit}"; return false; }
        if (to is null) { reason = $"UNIT_UNKNOWN:{canonicalUnit}"; return false; }
        if (!string.Equals(from.Dimension, to.Dimension, StringComparison.Ordinal))
        {
            reason = $"DIMENSION_MISMATCH:{from.Dimension}!={to.Dimension}";
            return false;
        }
        converted = value * from.ToCanonicalFactor / to.ToCanonicalFactor;
        if (!double.IsFinite(converted)) { reason = "NON_FINITE"; return false; }
        return true;
    }

    public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
