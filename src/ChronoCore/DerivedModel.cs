namespace ChronoCore;

/// <summary>单段速率区间（在该连续段内、相邻锚点之间）。</summary>
public sealed record RateSegment(
    string IntervalId,
    string FromAnchorId,
    string ToAnchorId,
    double DepthFromM, double DepthToM,
    double RateLow, double RateHigh,        // m/a，95% 联合不确定度
    double RateMedian,
    bool Valid)
{ public string StableId => $"rate-{IntervalId}-{FromAnchorId}-{ToAnchorId}"; }

/// <summary>不确定度带上的一个格点（全部带稳定身份）。</summary>
public sealed record EnvelopePoint(
    string IntervalId, double DepthM,
    double MedianAge, double Low95, double High95, double Low99, double High99)
{ public string StableId => $"env-{IntervalId}-{DepthM:0.###}"; }

/// <summary>一个可辩护深度-年龄候选模型。</summary>
public sealed record DerivedModel(
    string Id,
    string Family,                    // 谱系标识（同一输入下的等优候选共享）
    string Label,
    int Rank,                         // 稳定顺序（等价输入下确定）
    double FitScore,                  // 越小越优
    bool Tie,                         // 是否属于等优组
    IReadOnlyList<string> ChosenAnchorIds,
    IReadOnlyList<RateSegment> Rates,
    IReadOnlyList<EnvelopePoint> Envelope,
    IReadOnlyList<Diagnostic> Violations,
    string InputDigest,
    string ParameterVersion,
    IReadOnlyList<string> ParentDependencies,
    bool SemanticallyValid,
    string Notes)
{
    public bool Adoptable => SemanticallyValid;
}

public static class Hashing
{
    public static string Sha256(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
