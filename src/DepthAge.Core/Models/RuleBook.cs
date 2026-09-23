namespace DepthAge.Core.Models;

/// <summary>
/// 参数/规则版本。每次参数修订产生新版本，计算结果记录所用版本。
/// </summary>
public sealed record RuleBook
{
    public required string Version { get; init; }
    public required string Id { get; init; }
    public double MaxSamplesPerAnchor { get; init; } = 400;
    /// <summary>允许的最大候选数（枚举预算）。</summary>
    public int CandidateBudget { get; init; } = 64;
    /// <summary>等优阈值：分数差小于该值视为并列，全部保留。</summary>
    public double TieTolerance { get; init; } = 1e-6;
    /// <summary>采样重放基准种子（同规则版本 + 同输入 => 同分布）。</summary>
    public int SamplingSeedBase { get; init; } = 20260924;
    public string? ChangeNote { get; init; }
    public string? ParentVersion { get; init; }

    public static RuleBook Initial() => new()
    {
        Version = "rules-1.0.0",
        Id = StableId.Compute("rulebook", new { v = "rules-1.0.0", budget = 64, tie = 1e-6, seed = 20260924 }),
        CandidateBudget = 64,
        TieTolerance = 1e-6,
        SamplingSeedBase = 20260924,
        ChangeNote = "初始规则：线性/PAVA 双插值；并列全保留；缺口与不整合为硬边界"
    };

    public RuleBook Revised(int candidateBudget, double tieTolerance, string note)
    {
        var version = BumpVersion();
        return this with
        {
            Version = version,
            ParentVersion = Version,
            CandidateBudget = candidateBudget,
            TieTolerance = tieTolerance,
            ChangeNote = note,
            Id = StableId.Compute("rulebook", new { v = version, budget = candidateBudget, tie = tieTolerance, seed = SamplingSeedBase })
        };
    }

    private string BumpVersion()
    {
        var dash = Version.IndexOf('-');
        var prefix = dash < 0 ? "rules" : Version[..dash];
        var numeric = dash < 0 ? Version : Version[(dash + 1)..];
        var nums = numeric.Split('.').Select(int.Parse).ToArray();
        nums[^1]++;
        return $"{prefix}-{string.Join('.', nums)}";
    }
}
