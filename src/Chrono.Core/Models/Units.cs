using System.Globalization;

namespace Chrono.Core.Models;

/// <summary>A measured quantity. Raw representation is always preserved; a
/// normalized SI value is derived on a separate layer. Unconvertible values
/// stay at their original position with a diagnostic rather than being dropped.</summary>
public sealed record Qty
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public double? Value { get; init; }
    public string? NormUnit { get; init; }
    public double? NormValue { get; init; }
    public string? ConvertError { get; init; }

    public bool IsConvertible => NormValue is not null && ConvertError is null;

    public static Qty Parse(string raw, string expectedKind)
    {
        raw = (raw ?? string.Empty).Trim();
        var q = new Qty { Raw = raw, Unit = ExtractUnit(raw) };
        if (TryReadNumber(raw, out var num, out var rest))
            q = q with { Value = num, Unit = q.Unit.Length > 0 ? q.Unit : rest.Trim() };
        else
            return q with { ConvertError = $"无法解析数值（期望{expectedKind}）：'{raw}'，原值保留" };

        var table = expectedKind switch
        {
            "depth" => new Dictionary<string, (double f, string n)>(StringComparer.OrdinalIgnoreCase)
            {
                ["m"] = (1, "m"), ["meter"] = (1, "m"), ["meters"] = (1, "m"), ["米"] = (1, "m"),
                ["cm"] = (0.01, "m"), ["mm"] = (0.001, "m")
            },
            "age" => new Dictionary<string, (double f, string n)>(StringComparer.OrdinalIgnoreCase)
            {
                ["bp"] = (1, "yr BP"), ["yr bp"] = (1, "yr BP"), ["a bp"] = (1, "yr BP"),
                ["cal bp"] = (1, "yr BP"), ["yrbp"] = (1, "yr BP"),
                ["ka"] = (1000, "yr BP"), ["kyr"] = (1000, "yr BP"), ["ky"] = (1000, "yr BP"),
                ["ma"] = (1_000_000, "yr BP"), ["myr"] = (1_000_000, "yr BP")
            },
            "rate" => new Dictionary<string, (double f, string n)>(StringComparer.OrdinalIgnoreCase)
            {
                ["cm/kyr"] = (1, "cm/kyr"), ["cm/ka"] = (1, "cm/kyr"),
                ["m/ma"] = (0.1, "cm/kyr"),
                ["mm/yr"] = (100, "cm/kyr"), ["m/kyr"] = (100, "cm/kyr")
            },
            "offset" => new Dictionary<string, (double f, string n)>(StringComparer.OrdinalIgnoreCase)
            {
                ["yr"] = (1, "yr"), ["a"] = (1, "yr"), ["years"] = (1, "yr"),
                ["ka"] = (1000, "yr")
            },
            _ => new Dictionary<string, (double f, string n)>()
        };

        if (!table.TryGetValue(q.Unit, out var conv))
            return q with { ConvertError = $"未知或不兼容的{expectedKind}单位：'{q.Unit}'，原值保留，不参与绘图规范化" };
        return q with { NormUnit = conv.n, NormValue = num * conv.f };
    }

    private static bool TryReadNumber(string raw, out double num, out string rest)
    {
        num = 0; rest = string.Empty;
        int i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] is '.' or '-' or '+' or 'e' or 'E' or ',')) i++;
        var token = raw[..i].Replace(",", "");
        if (token.Length == 0 || token is "." or "-" or "+") return false;
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) return false;
        rest = raw[i..];
        return true;
    }

    private static string ExtractUnit(string raw)
    {
        var at = raw.IndexOfAny(new[] { ' ', '[' });
        return at < 0 ? string.Empty : raw[(at + 1)..].Trim('[', ']', ' ');
    }
}
