using System.Text.Json.Serialization;

namespace DepthAge.Core.Models;

public enum UnitKind
{
    Depth,
    Age,
    Rate,
    Dimensionless
}

/// <summary>带单位/坐标基准的领域量。原始表示始终保留；只有派生层才规范化。</summary>
public sealed record Quantity
{
    public required string RawText { get; init; }
    public double? Value { get; init; }
    public string? Unit { get; init; }
    public UnitKind? Kind { get; init; }
    public string? DatumId { get; init; }
    public double? CanonicalValue { get; init; }
    public string? CanonicalUnit { get; init; }
    public bool Converted { get; init; } = true;
    public List<string> Diagnostics { get; init; } = new();

    [JsonIgnore]
    public bool IsUsable => Converted && CanonicalValue.HasValue && Diagnostics.Count == 0;

    public double RequireCanonical() =>
        CanonicalValue ?? throw new InvalidOperationException($"量值无法规范化：{RawText} {Unit}（{string.Join("; ", Diagnostics)}）");
}
