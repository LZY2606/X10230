using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoCore.Quantities;

/// <summary>
/// A field value that always keeps the original textual representation.
/// Normalisation to the internal unit happens on a derived layer only;
/// values that cannot be converted stay in place with a diagnostic and are
/// never dropped for plotting convenience.
/// </summary>
public sealed record Measure
{
    [JsonPropertyName("raw")] public string Raw { get; init; } = "";
    [JsonPropertyName("unit")] public string? Unit { get; init; }
    [JsonPropertyName("si")] public double? Si { get; init; }
    [JsonPropertyName("diagnosticCode")] public string? DiagnosticCode { get; init; }
    [JsonPropertyName("diagnostic")] public string? Diagnostic { get; init; }

    public bool IsConvertible => DiagnosticCode is null && Si is not null;

    public static Measure Depth(string raw)
    {
        var parsed = ParseScalar(raw);
        if (parsed.DiagnosticCode is not null) return parsed;
        var factor = parsed.Unit?.ToLowerInvariant() switch
        {
            "m" or "mbsf" or "mcd" or "mbmp" => 1.0,
            "cm" or "cmcd" or "cmbsf" => 0.01,
            "mm" => 0.001,
            _ => (double?)null
        };
        if (factor is null)
            return parsed with
            {
                DiagnosticCode = "UNIT_UNKNOWN_DEPTH",
                Diagnostic = $"深度单位无法转换: '{parsed.Unit}'"
            };
        return parsed with { Si = parsed.Si!.Value * factor.Value };
    }

    public static Measure Age(string raw)
    {
        var parsed = ParseScalar(raw);
        if (parsed.DiagnosticCode is not null) return parsed;
        var factor = parsed.Unit?.ToLowerInvariant() switch
        {
            "cal_yr_bp" or "cal yr bp" or "yr bp" or "yrbp" or "a bp" => 1.0,
            "cal_ka_bp" or "ka" or "ka bp" or "cal ka bp" => 1000.0,
            "14c_yr_bp" or "14c yr bp" or "radiocarbon bp" => 1.0, // value kept; conversion needs reservoir+calibration rule
            _ => (double?)null
        };
        if (factor is null)
            return parsed with
            {
                DiagnosticCode = "UNIT_UNKNOWN_AGE",
                Diagnostic = $"年龄单位/基准无法转换: '{parsed.Unit}'"
            };
        return parsed with { Si = parsed.Si!.Value * factor.Value };
    }

    public static Measure OffsetYears(string raw)
    {
        var parsed = ParseScalar(raw);
        if (parsed.DiagnosticCode is not null) return parsed;
        var factor = parsed.Unit?.ToLowerInvariant() switch
        {
            "yr" or "years" or "a" or "14c yr" => 1.0,
            "ka" => 1000.0,
            _ => (double?)null
        };
        if (factor is null)
            return parsed with
            {
                DiagnosticCode = "UNIT_UNKNOWN_OFFSET",
                Diagnostic = $"偏移单位无法转换: '{parsed.Unit}'"
            };
        return parsed with { Si = parsed.Si!.Value * factor.Value };
    }

    private static Measure ParseScalar(string raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
            return new Measure
            {
                Raw = raw ?? "",
                DiagnosticCode = "VALUE_MISSING",
                Diagnostic = "值为空：不允许用空值或默认值伪装成功"
            };

        var splitAt = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetter(text[i]) || text[i] == '‰') { splitAt = i; break; }
        }

        var numberPart = (splitAt < 0 ? text : text[..splitAt]).Trim();
        var unitPart = (splitAt < 0 ? null : text[splitAt..].Trim());

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new Measure
            {
                Raw = raw,
                Unit = unitPart,
                DiagnosticCode = "VALUE_NOT_NUMERIC",
                Diagnostic = $"无法解析数值: '{numberPart}'"
            };

        if (double.IsNaN(value) || double.IsInfinity(value))
            return new Measure
            {
                Raw = raw,
                Unit = unitPart,
                DiagnosticCode = "VALUE_NOT_FINITE",
                Diagnostic = "数值必须有限"
            };

        return new Measure { Raw = raw, Unit = unitPart, Si = value };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
