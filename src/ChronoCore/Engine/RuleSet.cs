using ChronoCore.Quantities;

namespace ChronoCore.Engine;

/// <summary>
/// 模型规则与参数版本。任何参数修订都产生新版本，派生结果记录所用版本。
/// 放射性碳校正采用案例声明的线性近似：calBP ≈ 14C BP − ΔR，并叠加曲线误差。
/// </summary>
public sealed record ModelRuleSet(
    string Version,
    string CalibrationCurveId,
    double CurveErrorYears,
    int MonteCarloSamples,
    int CandidateBudget,
    string Note)
{
    public const string BaselineVersion = "rules/v1";

    public static readonly ModelRuleSet Baseline = new(
        BaselineVersion, "Marine20-linear", 45.0, 401, 2000,
        "初始规则：Marine20 线性近似，曲线误差 45 年，401 个确定性分位抽样。");
}
