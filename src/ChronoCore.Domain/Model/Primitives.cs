using System.Globalization;

namespace ChronoCore.Domain.Model;

/// <summary>
/// A measured quantity that preserves the exact source representation. The
/// raw token is never discarded; normalization only happens at the derived
/// layer, and values that cannot be converted stay in place with diagnostics.
/// </summary>
public sealed record Measure(double Value, string Unit, string Raw)
{
    public static Measure Parse(string token, string expectedKind)
    {
        var t = token.Trim();
        var split = SplitNumber(t);
        if (!double.TryParse(split.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"无法把“{token}”解析为{expectedKind}数值");
        if (string.IsNullOrWhiteSpace(split.Unit))
            throw new FormatException($"“{token}”缺少单位（{expectedKind}）");
        return new Measure(v, split.Unit.Trim(), t);
    }

    private static (string Number, string Unit) SplitNumber(string s)
    {
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        return (s[..i], s[i..]);
    }

    public string Canonical =>
        Value.ToString("G17", CultureInfo.InvariantCulture) + " " + Unit;
}

/// <summary>Result categories required by the acceptance contract: exhausted
/// budget, corrupted format and semantically unsolvable input are distinct.</summary>
public enum DiagSeverity
{
    /// <summary>Input could not be read as the declared domain format at all.</summary>
    CorruptFormat,

    /// <summary>Well-formed input but the geological constraints cannot be satisfied.</summary>
    SemanticUnsolvable,

    /// <summary>Parser/model engine hit an explicit resource limit.</summary>
    BudgetExhausted,

    /// <summary>Value kept in place; conversion to canonical units was not possible.</summary>
    Unconverted,

    /// <summary>Accepted but noteworthy (boundary case, conflict retained, etc.).</summary>
    Warning
}

/// <summary>A diagnostic pinned to an exact source location. Never replaced by
/// silent nulls or defaults.</summary>
public sealed record Diagnostic(
    DiagSeverity Severity,
    string Code,
    string Message,
    string SourceId,
    int Line,
    int Column);

/// <summary>Reference frame for a depth: axis unit, positive direction and the
/// named datum the depths are measured from.</summary>
public sealed record DepthDatum(
    string Id,
    string Name,
    string AxisUnit,
    DepthDirection Direction,
    double Depth,
    string Raw,
    IReadOnlyList<Diagnostic> Diagnostics);

public enum DepthDirection
{
    /// <summary>Depth increases downward below the datum (the usual coring convention).</summary>
    Downward = 0,

    /// <summary>Depth increases upward (height convention).</summary>
    Upward = 1
}

/// <summary>A depth on a named datum, preserving the original token.</summary>
public sealed record DepthRef(double Value, string Unit, string DatumId, string Raw)
{
    public string CanonicalKey =>
        Value.ToString("G17", CultureInfo.InvariantCulture) + "@" + Unit + "#" + DatumId;
}

/// <summary>The time scale a radiometric result is reported on.</summary>
public sealed record TimeBasis(string Scale, string CalendarOrEbp, string Raw);
