namespace Chrono.Engine;

/// <summary>建模参数（带版本）。参数修订产生新版本并触发局部重算。</summary>
public sealed record ModelParams(
    string Version,
    int SamplesPerAnchor,
    int MaxCandidates,
    double TailThresholdSigma,
    bool InversionRequiresDecision,
    double DuplicateDepthToleranceMetres)
{
    public const string InitialVersion = "params/initial";

    public static ModelParams Initial => new(InitialVersion, 400, 64, 3.0, true, 0.02);
}
