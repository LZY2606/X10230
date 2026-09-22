namespace StratAge.Core.Domain;

/// <summary>Where a piece of material came from inside the imported artefact.</summary>
public sealed record SourcePos(string File, string Sheet, int Row, string? Field = null)
{
    public override string ToString() => Field is null ? $"{File}/{Sheet}!R{Row}" : $"{File}/{Sheet}!R{Row}.{Field}";
}

public enum MaterialKind { DepthDatum, Segment, Gap, Anchor, ReservoirOffset, EventLayer, EventCorrelation, Exclusion, RuleSet }

public interface IMaterial
{
    string Id { get; }
    MaterialKind Kind { get; }
    SourcePos Pos { get; }
    bool Retracted { get; }
}

/// <summary>Depth datum / reference frame for a core. Immutable evidence.</summary>
public sealed record DepthDatum(string Id, string Core, string Datum, QValue DepthTop, QValue DepthBottom,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.DepthDatum;
}

/// <summary>A core section / lithostratigraphic segment with depth interval.</summary>
public sealed record Segment(string Id, string Core, string Name, QValue Top, QValue Bottom, string Lithology,
    SourcePos Pos, bool Inverted, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.Segment;
}

/// <summary>
/// A stratigraphic gap/unconformity. Gaps are explicit boundaries: monotonicity
/// and interpolation never cross them.
/// </summary>
public sealed record Gap(string Id, string Core, QValue Top, QValue Bottom, string GapType, string Basis,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.Gap;
}

public enum DistributionShape { Normal, LogNormal, Uniform, StudentT }

/// <summary>
/// A radiometric/age anchor. The uncertainty distribution is first-class evidence;
/// it must never be replaced by its mean on any derived layer.
/// </summary>
public sealed record AnchorAge(
    string Id, string Core, QValue Depth, QValue Age,
    DistributionShape Shape, double Param1, double Param2, double? Param3,
    string AgeMethod, string? ReservoirOffsetId,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.Anchor;
}

/// <summary>Reservoir age offset (e.g. marine reservoir correction) with its own distribution.</summary>
public sealed record ReservoirOffset(string Id, string Label, QValue Offset, DistributionShape Shape,
    double Param1, double Param2, string TimeBasis, SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.ReservoirOffset;
}

/// <summary>An event layer (tephra, turbidite, ...) that can correlate cores/sections.</summary>
public sealed record EventLayer(string Id, string Core, string Label, QValue Depth, string EventType,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.EventLayer;
}

/// <summary>Correlation assertion between two event layers.</summary>
public sealed record EventCorrelation(string Id, string EventAId, string EventBId, string Basis, double Confidence,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.EventCorrelation;
}

/// <summary>
/// An exclusion decision recorded at import time (rejected measurement, dismissed
/// tie, ...). All exclusions are retained - nothing is silently dropped.
/// </summary>
public sealed record Exclusion(string Id, string TargetId, MaterialKind TargetKind, string Reason,
    string? DecidedBy, SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.Exclusion;
}

/// <summary>Versioned rule set governing the derivation.</summary>
public sealed record RuleSetVersion(string Id, int Version, string MonotonicityPolicy, string TiePolicy,
    string TailPolicy, string GapPolicy, int MonteCarloDraws, int CandidateBudget, string Notes,
    SourcePos Pos, bool Retracted = false) : IMaterial
{
    public MaterialKind Kind => MaterialKind.RuleSet;
}

/// <summary>The immutable evidence layer for one core/case.</summary>
public sealed class MaterialSet
{
    public string CaseId { get; }
    public string Title { get; }
    public IReadOnlyList<IMaterial> Items { get; }
    public MaterialSet(string caseId, string title, IReadOnlyList<IMaterial> items)
    { CaseId = caseId; Title = title; Items = items; }

    public IEnumerable<T> Of<T>() where T : IMaterial => Items.OfType<T>().Where(m => !m.Retracted);
    public IEnumerable<IMaterial> OfKind(MaterialKind k) => Items.Where(m => m.Kind == k && !m.Retracted);
    public IMaterial? Find(string id) => Items.FirstOrDefault(m => m.Id == id);
    public MaterialSet WithRetracted(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        return new MaterialSet(CaseId, Title, Items
            .Select(m => set.Contains(m.Id) ? Retract(m) : m).ToList());
    }
    static IMaterial Retract(IMaterial m) => m switch
    {
        DepthDatum x => x with { Retracted = true },
        Segment x => x with { Retracted = true },
        Gap x => x with { Retracted = true },
        AnchorAge x => x with { Retracted = true },
        ReservoirOffset x => x with { Retracted = true },
        EventLayer x => x with { Retracted = true },
        EventCorrelation x => x with { Retracted = true },
        Exclusion x => x with { Retracted = true },
        RuleSetVersion x => x with { Retracted = true },
        _ => m
    };
}
