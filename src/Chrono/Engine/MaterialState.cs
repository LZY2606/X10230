using Chrono.Domain;

namespace Chrono.Engine;

/// <summary>事件物化后的当前状态：不可变材料层 + 可修改决定层 + 参数层。</summary>
public sealed class MaterialState
{
    public DepthDatum? Datum;
    public readonly Dictionary<string, CoreSection> Sections = new();
    public readonly Dictionary<string, SegmentBoundary> Boundaries = new();
    public readonly Dictionary<string, AgeAnchor> Anchors = new();
    public readonly Dictionary<string, ReservoirCorrection> Reservoirs = new();
    public readonly Dictionary<string, CalibrationCurve> Curves = new();
    public readonly Dictionary<string, EventBed> EventBeds = new();
    public readonly Dictionary<string, ExclusionDecision> Exclusions = new();
    public readonly Dictionary<string, string> AnchorSourceVersion = new();
    public ModelParams Params = ModelParams.Initial;
    public readonly HashSet<string> InversionDecisions = new();
    public readonly HashSet<string> FrozenBranches = new();

    public bool IsAnchorActive(string anchorId)
    {
        if (Exclusions.TryGetValue(anchorId, out var ex)) return !ex.Excluded;
        return true;
    }
}
