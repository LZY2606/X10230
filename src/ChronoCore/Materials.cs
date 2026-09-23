using System.Text.Json.Serialization;

namespace ChronoCore;

// ---- 不可变材料层：导入后只能追加修订，不能就地覆盖 -----------------------

/// <summary>深度基准（depth datum），所有深度量都显式引用一个基准。</summary>
public sealed record DepthDatum(string Id, string Name, string Unit, string Description, int Ord);

/// <summary>连续取心段；段内年龄必须随深度单调（向下变老）。</summary>
public sealed record CoreInterval(
    string Id, string Name,
    string DatumId,
    string TopDepthRaw, string BottomDepthRaw,
    int Ord,
    string? Note = null)
{
    [JsonIgnore] public Quantity Top => Quantity.Depth(TopDepthRaw, DatumId);
    [JsonIgnore] public Quantity Bottom => Quantity.Depth(BottomDepthRaw, DatumId);
}

/// <summary>深度缺口 / 不整合：显式边界，跨边界不做单调检查也不做插值。</summary>
public sealed record GapBoundary(
    string Id, string Type, // "recovery-gap" | "unconformity" | "fault"
    string DatumId, string AboveDepthRaw, string BelowDepthRaw,
    int Ord, string Reason);

/// <summary>放射性年龄分布：保留全部样本，绝不用均值替代。</summary>
public sealed record AgeSamples
{
    public string DistributionType { get; init; } = "empirical"; // empirical | normal | student-t
    public double[] SamplesCalABP { get; init; } = Array.Empty<double>();
    public int DegreesOfFreedom { get; init; } = 8; // 长尾 student-t
    public bool LongTail { get; init; }

    public int N => SamplesCalABP.Length;

    public double Quantile(double p)
    {
        if (N == 0) throw new InvalidOperationException("空分布");
        var s = (double[])SamplesCalABP.Clone();
        Array.Sort(s);
        // nearest-rank，p∈(0,1]
        var rank = Math.Clamp((int)Math.Ceiling(p * s.Length), 1, s.Length);
        return s[rank - 1];
    }
    public double Median => Quantile(0.5);
    public double Q025 => Quantile(0.025);
    public double Q975 => Quantile(0.975);
    public double Q005 => Quantile(0.005);
    public double Q995 => Quantile(0.995);
}

/// <summary>年龄锚点（深度 + 放射性/事件年龄分布）。</summary>
public sealed record Anchor(
    string Id, string SampleCode,
    string DepthRaw, string DatumId,
    AgeSamples Samples,
    string Method,
    int Ord,
    string? ReservoirOffsetId = null,
    string? EventLayerId = null,
    string? AlternativeGroupId = null, // 并列锚点组（并列判定 / 等优候选）
    string? Note = null);

/// <summary>储层（碳库）偏移及其不确定性，单位 cal a。</summary>
public sealed record ReservoirOffset(
    string Id, string Name,
    string DeltaRRaw, string UncertaintyRaw,
    bool Apply, int Ord, string Basis);

/// <summary>事件层（火山灰等）及其跨孔年龄相关性。</summary>
public sealed record EventLayer(
    string Id, string Name,
    string DepthRaw, string DatumId,
    string? ExternalAgeCalABPRaw,
    string CorrelationKey,   // 跨孔关联键
    int Ord, string SourceNote);

/// <summary>排除决定：被排除的材料永久保留并附理由。</summary>
public sealed record Exclusion(
    string Id, string MaterialId, string MaterialKind,
    string Reason, string DecidedBy, int Ord, string AtEventId);

// ---- 可修改决定层：参数版本与人工决定 -------------------------------------

/// <summary>建模参数的一个不可变版本。</summary>
public sealed record ParameterSet(
    string Version,
    string Interpolation, // "pchip" | "linear"
    bool UseReservoirCorrection,
    int MaxCandidates,
    double TailProbability,
    int Ord,
    string Note);

/// <summary>人工决定：接受/否定/调整候选，冲突决定成对保留。</summary>
public sealed record Decision(
    string Id, string Kind, // accept | reject | adjust | freeze-conflict
    string CandidateModelId,
    string? ReplacementParameterVersion,
    string Reason, string DecidedBy, int Ord);
