using System.Globalization;

namespace AgeDepth.Core;

/// <summary>
/// A measured quantity that keeps its original textual representation. Conversion to
/// the canonical unit/datum happens only in the derived layer; an unconvertible value
/// stays in place with a diagnostic and is never dropped for plotting.
/// </summary>
public sealed record QValue(
    string Raw,
    double? Canonical,
    string? CanonicalUnit,
    string? Datum,
    bool Convertible,
    IReadOnlyList<Diag> Diagnostics)
{
    public bool HasError { get; } = Diagnostics.Any(d => d.Severity == Severity.Error);

    public static QValue Unconvertible(string raw, string path, string reason, string? datum = null) =>
        new(raw, null, null, datum, false,
            new[] { new Diag("Q-UNCONVERTIBLE", Severity.Error, path, reason) });
}

/// <summary>Explicit unit tables. Unknown units or datum mismatches are reported, not guessed.</summary>
public static class Units
{
    public const string CanonicalDepthUnit = "m";
    public const string CanonicalAgeUnit = "yr BP1950";
    public const string CanonicalRateUnit = "cm/ka";

    private static readonly Dictionary<string, double> DepthFactor = new()
    {
        ["mm"] = 0.001, ["cm"] = 0.01, ["m"] = 1.0, ["mbsf"] = 1.0
    };

    private static readonly Dictionary<string, double> AgeFactor = new()
    {
        ["yr"] = 1, ["yrs"] = 1, ["year"] = 1, ["years"] = 1, ["a"] = 1,
        ["ka"] = 1000.0, ["kyr"] = 1000.0, ["ma"] = 1_000_000.0, ["myr"] = 1_000_000.0
    };

    private static readonly Dictionary<string, double> RateFactor = new()
    {
        ["cm/ka"] = 1.0, ["cm/kyr"] = 1.0, ["m/ma"] = 0.1, ["mm/yr"] = 100.0
    };

    private static readonly Dictionary<string, string> AgeDatumAlias = new()
    {
        ["bp"] = "BP1950", ["bp1950"] = "BP1950", ["cal bp"] = "BP1950",
        ["cal. bp"] = "BP1950", ["14c bp"] = "BP1950"
    };

    public static QValue ParseDepth(string raw, string path, string? datum)
    {
        var diags = new List<Diag>();
        var (num, unit) = SplitNumber(raw.Trim());
        if (num is null)
            return QValue.Unconvertible(raw, path, $"cannot parse depth token '{raw}'", datum);
        if (string.IsNullOrEmpty(unit))
        {
            diags.Add(new Diag("Q-UNIT-MISSING", Severity.Error, path,
                $"depth '{raw}' carries no unit; refusing to guess"));
            return new QValue(raw, null, null, datum, false, diags);
        }
        if (!DepthFactor.TryGetValue(unit.ToLowerInvariant(), out var f))
            return QValue.Unconvertible(raw, path, $"unknown depth unit '{unit}'", datum);
        if (string.IsNullOrWhiteSpace(datum))
            diags.Add(new Diag("Q-DATUM-MISSING", Severity.Warning, path,
                $"depth '{raw}' has no coordinate datum"));
        return new QValue(raw, num * f, CanonicalDepthUnit, datum, true, diags);
    }

    public static QValue ParseAge(string raw, string path, string? datum)
    {
        var (num, unit) = SplitNumber(raw.Trim());
        if (num is null)
            return QValue.Unconvertible(raw, path, $"cannot parse age token '{raw}'", datum);
        if (string.IsNullOrEmpty(unit))
            return new QValue(raw, null, null, datum, false, new[]
            {
                new Diag("Q-UNIT-MISSING", Severity.Error, path, $"age '{raw}' carries no unit")
            });
        if (!AgeFactor.TryGetValue(unit.ToLowerInvariant(), out var f))
            return QValue.Unconvertible(raw, path, $"unknown age unit '{unit}'", datum);
        if (string.IsNullOrWhiteSpace(datum))
            return new QValue(raw, null, null, datum, false, new[]
            {
                new Diag("Q-DATUM-MISSING", Severity.Error, path, $"age '{raw}' has no time datum")
            });
        if (!AgeDatumAlias.TryGetValue(datum.Trim().ToLowerInvariant(), out var canonicalDatum))
            return QValue.Unconvertible(raw, path,
                $"age time datum '{datum}' cannot be mapped to {CanonicalAgeUnit} without external calibration", datum);
        return new QValue(raw, num * f, CanonicalAgeUnit, canonicalDatum, true, Array.Empty<Diag>());
    }

    public static QValue ParseRate(string raw, string path)
    {
        var (num, unit) = SplitNumber(raw.Trim());
        if (num is null || string.IsNullOrEmpty(unit))
            return QValue.Unconvertible(raw, path, $"cannot parse rate token '{raw}'");
        if (!RateFactor.TryGetValue(unit.ToLowerInvariant(), out var f))
            return QValue.Unconvertible(raw, path, $"unknown rate unit '{unit}'");
        return new QValue(raw, num * f, CanonicalRateUnit, null, true, Array.Empty<Diag>());
    }

    private static (double? num, string unit) SplitNumber(string token)
    {
        var i = 0;
        while (i < token.Length && (char.IsDigit(token[i]) || token[i] is '.' or '-' or '+' or 'e' or 'E' or ',' or ' '))
            i++;
        var numText = token[..i].Trim().Replace(",", ".");
        var unit = token[i..].Trim();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) || double.IsNaN(num))
            return (null, unit);
        return (num, unit);
    }
}
