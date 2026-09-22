namespace Chrono.Core.Engine;

/// <summary>
/// Gaussian radiocarbon minus an independent Gaussian reservoir offset (convolution). Kept distinct
/// from the raw anchor distribution: reservoir correction is a derived quantity only.
/// </summary>
public sealed record ShiftedGaussianAge(double MeanKa, double SigmaKa, string RawText)
    : Model.GaussianAge(MeanKa, SigmaKa, RawText);
