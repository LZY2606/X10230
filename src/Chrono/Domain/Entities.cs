using System.Text.Json.Serialization;

namespace Chrono.Domain;

/// <summary>深度基准：所有深度量值的坐标基准。不可变材料。</summary>
public sealed record DepthDatum(string Id, string Name, string VerticalDatum, string PositiveDirection, string? Note);

/// <summary>岩芯分段（连续取芯段）。深度以原始表示 + 规范化米保留。</summary>
public sealed record CoreSection(
    string Id, string Code, QValue Top, QValue Base, string Recovery,
    IReadOnlyList<SourceCitation> Sources);

public sealed record SourceCitation(string MaterialId, string Field, string RawText, string? Locator);

public enum BoundaryKind
{
    /// <summary>对接（不断层段，允许跨边界插值）。</summary>
    Contact,
    /// <summary>深度缺口（缺段/取芯缺失，显式边界，禁止跨边界插值）。</summary>
    Gap,
    /// <summary>不整合面（显式时间边界，禁止跨边界插值）。</summary>
    Unconformity,
    /// <summary>断层（倒置层段的显式边界）。</summary>
    Fault
}

/// <summary>分段缺口/边界。Gap 与 Unconformity 是显式边界，年龄模型不得跨越它们插值。</summary>
public sealed record SegmentBoundary(
    string Id, BoundaryKind Kind, QValue Depth, string BetweenSectionTop, string BetweenSectionBase,
    QValue? MissingIntervalTop, QValue? MissingIntervalBase, string Rationale, IReadOnlyList<SourceCitation> Sources);

public enum AnchorKind
{
    Tephra, Radiocarbon, Biostrat, Magnetostrat, TiePoint, DirectObservation
}

/// <summary>
/// 年龄锚点。RawAgeText 永远保留；放射性碳锚点保存测量量，校准后的分布在派生层生成。
/// </summary>
public sealed record AgeAnchor(
    string Id, string Label, AnchorKind Kind, QValue Depth,
    string RawAgeText, string? AgeUnit,
    double? ConventionalBp, double? OneSigmaBp,
    AgeDistribution? GivenDistribution,
    string? ReservoirId, string? EventId,
    bool InvertedFlag,
    string? TieGroup,
    IReadOnlyList<SourceCitation> Sources);

/// <summary>储层偏移（ΔR）。可带自身不确定度，以参数版本参与校准。</summary>
public sealed record ReservoirCorrection(
    string Id, string Name, QValue OffsetYears, QValue SigmaYears,
    string MarineCurve, string ParameterVersion, string? Note);

/// <summary>校准曲线点（cal BP 年 ↔ 放射性碳 BP）。案例包内自带，离线可用。</summary>
public sealed record CalibrationCurve(string Id, string Name, IReadOnlyList<CalPoint> Points)
{
    public bool TryLookup(double carbBp, out (double calBp, double slope) hit)
    {
        for (var i = 0; i < Points.Count - 1; i++)
        {
            var a = Points[i];
            var b = Points[i + 1];
            var lo = Math.Min(a.CarbBp, b.CarbBp);
            var hi = Math.Max(a.CarbBp, b.CarbBp);
            if (carbBp >= lo && carbBp <= hi)
            {
                var t = (carbBp - a.CarbBp) / (b.CarbBp - a.CarbBp);
                var slope = (b.CalBp - a.CalBp) / (b.CarbBp - a.CarbBp);
                hit = (a.CalBp + t * (b.CalBp - a.CalBp), slope);
                return true;
            }
        }
        hit = default;
        return false;
    }
}

public sealed record CalPoint(double CalBp, double CarbBp);

/// <summary>事件层（火山灰、生物事件等）：锚点与事件的相关关系不可丢失。</summary>
public sealed record EventBed(
    string Id, string Name, string EventType, QValue Depth,
    string? CorrelatedGlobalEvent, string Confidence, string? Note);

/// <summary>排除决定（再搬运壳、污染样等）。决定本身与理由、决定者、时间都保留。</summary>
public sealed record ExclusionDecision(
    string Id, string AnchorId, string Reason, string DecidedBy, string DecidedAt,
    bool Excluded, string? SupersededDecisionId);
