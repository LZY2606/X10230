using System.Globalization;

namespace GSB.Core.Quantities;

/// <summary>
/// 领域量。<see cref="Raw"/> 始终保留导入时的原始表示（含原始单位）；
/// 只有派生层才规范化到 <see cref="CanonicalUnit"/>。
/// 无法转换时 <see cref="CanonicalValue"/> 为 null 并带诊断，原始值仍留在原处。
/// </summary>
public sealed record Measure(double? CanonicalValue, string CanonicalUnit, string Raw, string? RawUnit)
{
    public bool Convertible => CanonicalValue.HasValue;

    public static Measure FromCanonical(double value, string unit, string? raw = null, string? rawUnit = null)
        => new(value, unit, raw ?? value.ToString("R", CultureInfo.InvariantCulture), rawUnit ?? unit);

    /// <summary>
    /// 从原始表示构造：value 是原单位数值，unit 是原单位字符串。
    /// 未知单位不会被静默当作规范单位，而是返回不可转换度量并附诊断。
    /// </summary>
    public static Measure Parse(double value, string unit, QuantityKind kind)
    {
        var raw = value.ToString("R", CultureInfo.InvariantCulture) + " " + unit;
        if (UnitTable.TryGetFactor(kind, unit, out var factor, out var canonical))
            return new Measure(CanonicalValue: value * factor, CanonicalUnit: canonical, Raw: raw, RawUnit: unit);
        return new Measure(CanonicalValue: null, CanonicalUnit: canonical, Raw: raw, RawUnit: unit);
    }

    public string DiagnosticCode => Convertible ? "ok" : "quantity.unconvertible-unit";

    public override string ToString() => Raw;
}

public enum QuantityKind { Length, Age, Rate }
