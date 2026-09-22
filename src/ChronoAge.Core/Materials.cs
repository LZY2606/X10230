using System.Text.Json.Serialization;

namespace ChronoAge.Core;

/// <summary>
/// Immutable source material. These rows are never mutated in place: a revision appends a
/// new version with the same <see cref="LogId"/>. Exclusions are separate decisions, so an
/// excluded material is retained in full.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DepthDatum), "depth_datum")]
[JsonDerivedType(typeof(CoreSection), "section")]
[JsonDerivedType(typeof(SegmentBoundary), "boundary")]
[JsonDerivedType(typeof(RadiometricAssay), "assay")]
[JsonDerivedType(typeof(DirectAnchor), "anchor")]
[JsonDerivedType(typeof(ReservoirOffset), "reservoir")]
[JsonDerivedType(typeof(EventLayer), "event_layer")]
[JsonDerivedType(typeof(EventCorrelation), "event_correlation")]
public abstract record Material(
    string Id,
    string LogId,
    int Version,
    string Source,
    string? SupersededBy)
{
    public abstract string KindCode { get; }
    public abstract IEnumerable<Diagnostic> Validate();
}

/// <summary>Depth datum declaration (e.g. sea floor) with the time-space basis recorded explicitly.</summary>
public sealed record DepthDatum(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Name,
    string Basis,
    double? OffsetM)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "depth_datum";
    public override IEnumerable<Diagnostic> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            yield return Diag.Error("DATUM_NO_NAME", "Depth datum has no name.", Id, "name");
        if (string.IsNullOrWhiteSpace(Basis))
            yield return Diag.Error("DATUM_NO_BASIS", "Depth datum has no stated coordinate basis.", Id, "basis");
    }
}

/// <summary>A recovered core section expressed in the declared datum. Both ends carry original tokens.</summary>
public sealed record CoreSection(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Code,
    Measure Top,
    Measure Bottom,
    string DatumName)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "section";
    public override IEnumerable<Diagnostic> Validate()
    {
        foreach (var d in Top.Diagnostics) yield return d;
        foreach (var d in Bottom.Diagnostics) yield return d;
        if (Top.Canonical.HasValue && Bottom.Canonical.HasValue && Bottom.Canonical <= Top.Canonical)
            yield return Diag.Error("SEC_BAD_SPAN", $"Section {Code}: bottom ({Bottom.Raw}) must be deeper than top ({Top.Raw}).", Id, "bottom");
    }
}

public enum BoundaryKind { Contact, Gap, Unconformity }

/// <summary>
/// Explicit boundary between sections. Gaps and unconformities are first-class edges:
/// monotonicity is never enforced across them, and missing time is not interpolated away.
/// </summary>
public sealed record SegmentBoundary(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string UpperSection,
    string LowerSection,
    BoundaryKind Kind,
    Measure? GapThickness,
    string? Note)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "boundary";
    public bool IsDepositionalBreak => Kind != BoundaryKind.Contact;
    public override IEnumerable<Diagnostic> Validate()
    {
        if (GapThickness is not null)
            foreach (var d in GapThickness.Diagnostics) yield return d;
        if (Kind == BoundaryKind.Contact && GapThickness?.Canonical is > 0)
            yield return Diag.Warn("BND_CONTACT_GAP", "A 'contact' boundary carries a non-zero gap thickness; check classification.", Id, "gap");
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "dist")]
[JsonDerivedType(typeof(NormalSpec), "normal")]
[JsonDerivedType(typeof(LogNormalSpec), "lognormal")]
[JsonDerivedType(typeof(EmpiricalSpec), "empirical")]
[JsonDerivedType(typeof(UniformSpec), "uniform")]
public abstract record AgeDistributionSpec;

/// <summary>Symmetric Gaussian age uncertainty (yr BP canonical).</summary>
public sealed record NormalSpec(double Mean, double Sigma) : AgeDistributionSpec;

/// <summary>Right-skewed (long-tail) age uncertainty — keeps the tail, never collapses to a mean.</summary>
public sealed record LogNormalSpec(double MuLog, double SigmaLog) : AgeDistributionSpec;

/// <summary>Raw empirical samples (e.g. a radiocarbon calibration posterior). Retained verbatim.</summary>
public sealed record EmpiricalSpec(IReadOnlyList<double> Samples, string? Unit) : AgeDistributionSpec;

/// <summary>Flat support over [Min,Max] — e.g. a hiatus span without further evidence.</summary>
public sealed record UniformSpec(double Min, double Max) : AgeDistributionSpec;

/// <summary>
/// A radiometric date on material at a depth. For uncalibrated 14C the measured value stays
/// uncalibrated; an age distribution is derived only through parameters + reservoir offset.
/// </summary>
public sealed record RadiometricAssay(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Code,
    Measure Depth,
    string MaterialKind,
    bool IsCalibrated,
    double? ReportedAge,
    double? ReportedSigma,
    string? ReportedAgeUnit,
    AgeDistributionSpec? CalibratedDist,
    string? ReservoirLogId)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "assay";
    public override IEnumerable<Diagnostic> Validate()
    {
        foreach (var d in Depth.Diagnostics) yield return d;
        if (!IsCalibrated && ReportedAge is null)
            yield return Diag.Error("ASSAY_NO_AGE", "Uncalibrated assay carries no reported age.", Id, "reported_age");
        if (IsCalibrated && CalibratedDist is null)
            yield return Diag.Error("ASSAY_NO_DIST", "Calibrated assay must carry its full distribution, not a mean only.", Id, "calibrated_dist");
        if (IsCalibrated && CalibratedDist is NormalSpec n && n.Sigma <= 0)
            yield return Diag.Error("ASSAY_BAD_SIGMA", "Calibrated normal distribution needs a positive sigma.", Id, "calibrated_dist");
    }
}

/// <summary>An age anchor entered directly as a distribution (the distribution is the evidence).</summary>
public sealed record DirectAnchor(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Code,
    Measure Depth,
    AgeDistributionSpec Distribution,
    string? Basis)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "anchor";
    public override IEnumerable<Diagnostic> Validate()
    {
        foreach (var d in Depth.Diagnostics) yield return d;
        if (Distribution is null)
            yield return Diag.Error("ANC_NO_DIST", "Anchor must carry a distribution; a mean alone is not accepted.", Id, "distribution");
    }
}

/// <summary>Reservoir offset R (mean + uncertainty). Applied to 14C dates of the stated material.</summary>
public sealed record ReservoirOffset(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Name,
    string AppliesToMaterial,
    double MeanYears,
    double SigmaYears)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "reservoir";
    public override IEnumerable<Diagnostic> Validate()
    {
        if (SigmaYears < 0)
            yield return Diag.Error("RES_NEG_SIGMA", "Reservoir offset uncertainty cannot be negative.", Id, "sigma");
    }
}

/// <summary>A marker event (tephra etc.) at a depth. Its age may be unknown until correlated.</summary>
public sealed record EventLayer(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string Code,
    Measure Depth,
    string Lithology)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "boundary_event";
    public override IEnumerable<Diagnostic> Validate()
    {
        foreach (var d in Depth.Diagnostics) yield return d;
    }
}

/// <summary>Correlation tying an event layer to an external dated event, carrying its distribution.</summary>
public sealed record EventCorrelation(
    string Id, string LogId, int Version, string Source, string? SupersededBy,
    string EventLogId,
    string ExternalName,
    string ExternalBasis,
    AgeDistributionSpec AgeDistribution)
    : Material(Id, LogId, Version, Source, SupersededBy)
{
    public override string KindCode => "event_correlation";
    public override IEnumerable<Diagnostic> Validate()
    {
        if (AgeDistribution is null)
            yield return Diag.Error("CORR_NO_DIST", "Event correlation must carry the external age distribution.", Id, "age");
    }
}
