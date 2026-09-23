using System.Text.Json.Serialization;

namespace Chrono.Core.Domain;

// 坐标/时间基准与原始表示一起保留；只有派生层才规范化数值。
public sealed record QValue
{
    public required string Raw { get; init; }
    public double? Value { get; init; }
    public string? Unit { get; init; }
    public string? Basis { get; init; }          // mbsf / mCD ... 坐标基准
    public string? ValidRange { get; init; }     // 有效区间（领域约束）
    public string? Diagnostic { get; init; }     // 无法转换时留在原位的诊断
    public bool IsUsable => Value.HasValue && string.IsNullOrEmpty(Diagnostic);
}

public enum DistributionKind { Normal, Samples }

// 锚点年龄始终是分布；派生层才取分位数，均值不能替代分布。
public sealed record AgeDistribution
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }   // "cal yr BP"
    public required string Basis { get; init; }  // "IntCal20" / "Marine20"
    public DistributionKind Kind { get; init; }
    public double? Mean { get; init; }
    public double? Sigma { get; init; }
    public double[]? Samples { get; init; }
    public string? Diagnostic { get; init; }
    public bool LongTail { get; init; }
    public bool IsUsable => string.IsNullOrEmpty(Diagnostic) &&
        (Kind == DistributionKind.Normal ? Mean.HasValue && Sigma.HasValue : Samples is { Length: > 0 });

    // 确定性分位数：样本用排序+线性插值，正态用确定性有理逼近，不依赖随机数。
    public double Quantile(double p)
    {
        p = Math.Clamp(p, 0d, 1d);
        if (Kind == DistributionKind.Samples && Samples is { Length: > 0 } s)
        {
            Array.Sort(s);
            if (s.Length == 1) return s[0];
            double pos = p * (s.Length - 1);
            int i = (int)Math.Floor(pos);
            double f = pos - i;
            if (i >= s.Length - 1) return s[^1];
            return s[i] * (1 - f) + s[i + 1] * f;
        }
        if (Mean.HasValue && Sigma.HasValue) return Mean.Value + Sigma.Value * NormalInv(p);
        throw new InvalidOperationException("distribution unusable: " + Diagnostic);
    }

    public AgeDistribution ShiftYears(double delta, string note) => this with
    {
        Mean = Mean.HasValue ? Mean.Value - delta : null,
        Samples = Samples?.Select(v => v - delta).ToArray(),
        Raw = Raw + $" | reservoir-corrected({note})",
    };

    private static double NormalInv(double p)
    {
        // Abramowitz & Stegun 26.2.23，确定性近似（|误差| < 4.5e-4）
        const double c0 = 2.515517, c1 = 0.802853, c2 = 0.010328;
        const double d1 = 1.432788, d2 = 0.189269, d3 = 0.001308;
        double sign = p < 0.5 ? -1 : 1;
        double t = Math.Sqrt(-2 * Math.Log(Math.Min(p, 1 - p)));
        double z = t - (c0 + c1 * t + c2 * t * t) / (1 + d1 * t + d2 * t * t + d3 * t * t * t);
        return sign * z;
    }
}

public enum BoundaryKind { Conformable, Gap, Unconformity, Fault }
public enum CorrelationStatus { Correlated, Tentative, Rejected }
public enum DecisionVote { Keep, Exclude }

public sealed record SourceLocation
{
    public required string File { get; init; }
    public int Line { get; init; }
    public int Offset { get; init; }
    public string? Fragment { get; init; }
}

public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Basis { get; init; }   // 深度基准说明
    public SourceLocation? Source { get; init; }
}

public sealed record Segment
{
    public required string Id { get; init; }
    public required string Lithology { get; init; }
    public required QValue TopDepth { get; init; }
    public required QValue BottomDepth { get; init; }
    public int Order { get; init; }
    public SourceLocation? Source { get; init; }
}

public sealed record Boundary
{
    public required string Id { get; init; }
    public string? FromSegmentId { get; init; }
    public string? ToSegmentId { get; init; }
    public required BoundaryKind Kind { get; init; }
    public QValue? GapEstimate { get; init; }
    public string? EventLayerId { get; init; }
    public string? Evidence { get; init; }
    public SourceLocation? Source { get; init; }
}

public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required QValue Depth { get; init; }
    public required AgeDistribution Age { get; init; }
    public required string Material { get; init; }      // charcoal / marine shell ...
    public string? ReservoirOffsetId { get; init; }
    public string? EventLayerId { get; init; }
    public bool Mandatory { get; init; }                 // 事件层锁定/人工 Keep
    public required string SourceRef { get; init; }
    public SourceLocation? Source { get; init; }
}

public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string AppliesToMaterial { get; init; }
    public required double DeltaRYears { get; init; }
    public double? SigmaYears { get; init; }
    public required string Citation { get; init; }
    public SourceLocation? Source { get; init; }
}

public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required QValue Depth { get; init; }
    public required AgeDistribution PublishedAge { get; init; }
    public required CorrelationStatus Correlation { get; init; }
    public required string Citation { get; init; }
    public SourceLocation? Source { get; init; }
}

public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string SubjectKind { get; init; }  // Anchor / Event
    public required string SubjectId { get; init; }
    public required DecisionVote Vote { get; init; }
    public required string Reason { get; init; }
    public required string Actor { get; init; }
    public long EventSeq { get; init; }
    public bool ConflictsWithPrior { get; init; }
}

public sealed record RulesParams
{
    public required string Version { get; init; }
    public double DepthRepeatToleranceM { get; init; } = 0.01;
    public double TieAgeToleranceYr { get; init; } = 150;
    public long EnumerationBudget { get; init; } = 20_000;
    public string? Notes { get; init; }
}
