using ChronoCore.Domain;

namespace ChronoWeb.Services;

public static class ProjectDto
{
    public static object Material(Material m) => new
    {
        m.Id, kind = m.Kind.ToString(), m.SourceRef, m.SourceLine, m.SourceFragment,
        m.SampleCode, m.LabCode, depthDatum = m.DepthDatum.ToString(),
        depthRaw = Text(m.DepthRaw), depthSiMbsf = m.DepthRaw?.ValueSi,
        ageRaw = Text(m.AgeRaw), ageSiYrBp = m.AgeRaw?.ValueSi,
        sigmaRaw = Text(m.OneSigmaRaw), ageScale = m.AgeScale.ToString(),
        m.Distribution, m.ShapeK, m.ScaleTheta, m.MinClamp, m.TailWeight,
        m.ReservoirId, m.MaterialType, m.SegmentCode, m.Lithology,
        boundaryAtBase = m.BoundaryAtBase.ToString(), m.OverturnedSection,
        m.ReservoirName, deltaR = Text(m.DeltaR),
        reservoirValidFrom = Text(m.ValidFromAge), reservoirValidTo = Text(m.ValidToAge),
        m.EventName, eventDepthRaw = Text(m.EventDepthRaw),
        eventAgeRaw = Text(m.EventAgeRaw), m.CorrelationBasis,
        m.Excluded, m.ExclusionReason,
        diagnostic = m.DepthRaw?.Diagnostic ?? m.AgeRaw?.Diagnostic ?? m.OneSigmaRaw?.Diagnostic
    };

    private static object? Text(Measurement? q) => q is null ? null
        : new { q.Raw, q.Unit, valueSi = q.ValueSi, q.Diagnostic, convertible = q.IsConvertible };
}
