using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chrono.Domain;

/// <summary>
/// 所有领域量都保留原始表示；只有派生层才尝试规范化。
/// 无法转换时 <see cref="Normalized"/> 为 null 并附带诊断，原值留在原处。
/// </summary>
public sealed record RawQuantity
{
    public required string Raw { get; init; }
    public string? Unit { get; init; }
    public string? Basis { get; init; }
    public double? Normalized { get; init; }
    public string? NormalizedUnit { get; init; }
    public List<string> Diagnostics { get; init; } = new();

    public bool IsConvertible => Normalized is not null;
}

/// <summary>带位置的输入诊断：不得用空值或默认值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    string? MaterialId = null,
    string? Field = null,
    int? Line = null,
    string? Severity = null)
{
    public string SeverityOrError => Severity ?? "error";
}

public enum OutcomeKind
{
    Ok = 0,
    /// <summary>格式损坏：JSON 无法解析、必填结构缺失等。</summary>
    Corrupt = 1,
    /// <summary>语义无解：例如不断层段内年龄倒置且未声明倒转。</summary>
    SemanticImpossibility = 2,
    /// <summary>资源预算耗尽：采样数/候选组合数超过上限。</summary>
    BudgetExhausted = 3
}

public static class QuantityParser
{
    // 深度单位 -> 米（规范化为 m，基准另外保留）
    private static readonly Dictionary<string, double> DepthToMeter = new()
    {
        ["m"] = 1.0, ["m bsf"] = 1.0, ["mbsf"] = 1.0, ["m b.d."] = 1.0,
        ["cm"] = 0.01, ["mm"] = 0.001,
        ["ft"] = 0.3048, ["feet"] = 0.3048
    };

    public static RawQuantity ParseDepth(JsonElement el, string field, int? line = null)
    {
        var diag = new List<string>();
        string? unit = el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        string? basis = el.TryGetProperty("basis", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string raw = el.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!
            : el.GetRawText();

        if (!el.TryGetProperty("value", out var v) || v.ValueKind == JsonValueKind.Null)
        {
            diag.Add($"DEPTH_NO_VALUE:{field}:深度缺少数值，保留原始表示 '{raw}'");
            return new RawQuantity { Raw = raw, Unit = unit, Basis = basis, Diagnostics = diag };
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var num))
        {
            diag.Add($"DEPTH_UNCONVERTIBLE:{field}:深度值 '{v}' 无法转换为数值，保留原位置");
            return new RawQuantity { Raw = raw, Unit = unit, Basis = basis, Diagnostics = diag };
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            diag.Add($"DEPTH_NO_UNIT:{field}:深度 '{raw}' 未声明单位，无法规范化，保留原位置");
            return new RawQuantity { Raw = raw, Unit = unit, Basis = basis, Diagnostics = diag };
        }

        var key = unit.Trim().ToLowerInvariant();
        if (!DepthToMeter.TryGetValue(key, out var factor))
        {
            diag.Add($"DEPTH_UNKNOWN_UNIT:{field}:未知深度单位 '{unit}'，无法规范化，保留原位置");
            return new RawQuantity { Raw = raw, Unit = unit, Basis = basis, Diagnostics = diag };
        }

        if (num < 0)
            diag.Add($"DEPTH_NEGATIVE:{field}:深度 {num} {unit} 为负值（相对基准向上？），仍规范化但标记");

        return new RawQuantity
        {
            Raw = raw,
            Unit = unit,
            Basis = basis,
            Normalized = num * factor,
            NormalizedUnit = "m"
        };
    }

    public static bool InvariantDouble(string s, out double d) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
}

/// <summary>时间基准（不混用 14C 年与日历年）。</summary>
public sealed record AgeFrame(string Kind, string Scale, string? Epoch)
{
    // Kind: "radiocarbon"（未校正 14C 年）或 "calendar"（校正后日历年）
    public static readonly AgeFrame RadiocarbonBP = new("radiocarbon", "yr BP", null);
    public static readonly AgeFrame CalendarBP1950 = new("calendar", "yr BP", "BP1950");
}
