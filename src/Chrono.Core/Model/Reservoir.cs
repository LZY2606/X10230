namespace Chrono.Core.Model;

/// <summary>
/// Marine/carbon reservoir correction. Carries its own offset distribution and reference (e.g.
/// ΔR vs Marine20); applied only in the derived prepared anchor, raw radiocarbon stays untouched.
/// </summary>
public sealed record ReservoirOffset(
    string Id,
    double MeanKa,
    double SigmaKa,
    string ReferenceCurve,
    string RawText);
