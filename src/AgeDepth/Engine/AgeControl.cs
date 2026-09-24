using AgeDepth.Domain;

namespace AgeDepth.Engine;

internal sealed record AgeControl(
    string Id,
    string SegmentId,
    double Depth,
    IReadOnlyList<double> AgeSamples,
    IReadOnlyList<double>? ReservoirSamples,
    bool Excluded,
    string Kind);
