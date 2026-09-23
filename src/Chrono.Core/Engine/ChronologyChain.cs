using Chrono.Core.Materials;

namespace Chrono.Core.Engine;

public sealed record ChronologyChain(
    string ChainId,
    string SegmentId,
    string Label,
    int Order,
    double TopMeters,
    double BottomMeters,
    bool Unbroken,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<string> ViolationCodes)
{
    public bool InRange(double depth) => depth >= TopMeters - 1e-7 && depth <= BottomMeters + 1e-7;

    public ChronologyChain WithAnchors(IReadOnlyList<Anchor> anchors, IReadOnlyList<string> violations)
        => this with { Anchors = anchors, ViolationCodes = violations };

    public ChronologyChain Rebrand(string chainId, int order)
        => this with { ChainId = chainId, Order = order };
}
