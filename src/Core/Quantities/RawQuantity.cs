namespace AgeModel.Core.Quantities;

/// <summary>
/// A quantity exactly as imported: original textual representation, declared unit and
/// datum are preserved verbatim. Normalization to canonical units happens only in the
/// derived layer; unconvertible values stay in place with a diagnostic and are never
/// dropped for the sake of plotting.
/// </summary>
public sealed record RawQuantity
{
    public required string Raw { get; init; }
    public string? Unit { get; init; }
    public string? Datum { get; init; }

    public static RawQuantity Of(string raw, string? unit = null, string? datum = null) =>
        new() { Raw = raw, Unit = unit, Datum = datum };

    public bool TryParseNumber(out double value)
    {
        var text = Raw.Trim();
        if (text.Length == 0) { value = 0; return false; }
        // accept leading numeric token; remainder is ignored but preserved in Raw
        var span = text.AsSpan();
        var end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] is '.' or '-' or '+' or 'e' or 'E')) end++;
        if (end == 0) { value = 0; return false; }
        return double.TryParse(span[..end], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public override string ToString() =>
        Unit is null ? Raw : $"{Raw} {Unit}";
}

/// <summary>A canonicalized value: numeric value in canonical unit plus the datum it refers to.</summary>
public sealed record CanonicalQuantity(double Value, string CanonicalUnit, string? Datum)
{
    public override string ToString() => $"{UnitCatalog.Format(Value)} {CanonicalUnit}{(Datum is null ? "" : " @" + Datum)}";
}
