namespace Chrono.Core.Units;

public enum RawParseStatus { Parsed, Unconverted, Malformed }

public sealed record RawToken(
    string SourceId,
    string Field,
    string RawText,
    RawParseStatus Status,
    string? CanonicalUnit,
    string? Diagnostic)
{
    public bool IsUsable => Status == RawParseStatus.Parsed;
}

public sealed record ParseOutcome<T>(T? Value, RawToken Token) where T : struct
{
    public bool IsUsable => Token.IsUsable && Value.HasValue;
}

public static class RawParser
{
    public static ParseOutcome<DepthValue> ParseDepth(string sourceId, string field, string? rawText, VerticalDatum datum)
    {
        var token = new RawToken(sourceId, field, rawText ?? string.Empty, RawParseStatus.Malformed, null, null);
        if (string.IsNullOrWhiteSpace(rawText))
            return new ParseOutcome<DepthValue>(null, token with { Diagnostic = "depth is empty" });

        var parts = rawText.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var firstPart = parts.Length == 0 ? "" : parts[0];
        if (!double.TryParse(firstPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return new ParseOutcome<DepthValue>(null,
                token with { Diagnostic = "cannot parse numeric part '" + firstPart + "'" });

        string unitText = parts.Length > 1 ? parts[1] : UnitConversions.Symbol(datum.DisplayUnit);
        if (!UnitConversions.TryParseLengthUnit(unitText, out var unit))
            return new ParseOutcome<DepthValue>(null, token with
            {
                Status = RawParseStatus.Unconverted,
                Diagnostic = $"unit '{unitText}' is not a recognized length unit; value kept at source position"
            });

        var meters = UnitConversions.ToMeters(number, unit);
        return new ParseOutcome<DepthValue>(new DepthValue(meters),
            token with { Status = RawParseStatus.Parsed, CanonicalUnit = UnitConversions.Symbol(unit) });
    }
}
