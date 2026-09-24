using System.Text.Json.Nodes;

namespace Chrono.Engine;

/// <summary>
/// 参数是可修改决定，独立于不可变材料版本化。
/// 规则版本与参数版本分开：改规则 -> ruleVersion；改参数 -> paramsVersion 递增。
/// </summary>
public sealed record ChronoParameters(
    string ParamsVersion,
    string RuleVersion,
    int MonteCarloSamples,
    int MaxCandidates,
    bool ApplyReservoirCorrection,
    double RateFloorMPerKa,
    double RateCeilingMPerKa,
    long SamplingSeed)
{
    public const string InitialRuleVersion = "rules-2026.1";

    public JsonNode ToJson() => new JsonObject
    {
        ["paramsVersion"] = ParamsVersion,
        ["ruleVersion"] = RuleVersion,
        ["monteCarloSamples"] = MonteCarloSamples,
        ["maxCandidates"] = MaxCandidates,
        ["applyReservoirCorrection"] = ApplyReservoirCorrection,
        ["rateFloorMPerKa"] = RateFloorMPerKa,
        ["rateCeilingMPerKa"] = RateCeilingMPerKa,
        ["samplingSeed"] = SamplingSeed
    };

    public static ChronoParameters Default() => new(
        "params-1", InitialRuleVersion,
        MonteCarloSamples: 2000,
        MaxCandidates: 64,
        ApplyReservoirCorrection: true,
        RateFloorMPerKa: 0.0,
        RateCeilingMPerKa: 100.0,
        SamplingSeed: 20260925L);

    /// <summary>资源预算：采样上限 60_000、候选组合上限 4096。</summary>
    public const int HardSampleCap = 60_000;
    public const int HardCandidateCap = 4096;
}
