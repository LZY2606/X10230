namespace AgeDepth.Core;

/// <summary>
/// Immutable materials. Once imported they are never mutated; every
/// correction arrives as a new material or as a decision layered on top.
/// Each material keeps its raw source text so the original representation
/// survives import, export and re-import unchanged.
/// </summary>
public abstract record Material(string Id)
{
    /// <summary>Original source representation, preserved verbatim.</summary>
    public string Raw { get; init; } = "";
}

/// <summary>Depth datum / coordinate basis, e.g. mbsf (metres below seafloor).</summary>
public sealed record DepthDatum(
    string Id,
    string Name,
    string Basis,
    string Unit) : Material(Id);

/// <summary>A continuously recovered core interval. Gaps between sections are explicit boundaries.</summary>
public sealed record CoreSection(
    string Id,
    string DatumId,
    Quantity Top,
    Quantity Bottom,
    string CoreLabel) : Material(Id);

/// <summary>An explicit hiatus surface: depth gap or unconformity.</summary>
public sealed record HiatusBoundary(
    string Id,
    string DatumId,
    Quantity Depth,
    HiatusKind Kind,
    string Note) : Material(Id);

public enum HiatusKind { Gap, Unconformity }

/// <summary>
/// A radiometric age anchor. The full age distribution is the material;
/// it is never replaced by its mean.
/// </summary>
public sealed record AgeAnchor(
    string Id,
    string DatumId,
    Quantity Depth,
    AgeDistribution Age,
    string? ReservoirOffsetId,
    string LabCode) : Material(Id);

/// <summary>A full age distribution as posterior samples with a unit/basis.</summary>
public sealed record AgeDistribution(
    string Unit,
    string Basis,
    double[] Samples)
{
    public double Quantile(double p)
    {
        var s = Samples.OrderBy(x => x).ToArray();
        if (s.Length == 0) return double.NaN;
        var idx = p * (s.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = Math.Min(lo + 1, s.Length - 1);
        var frac = idx - lo;
        return s[lo] * (1 - frac) + s[hi] * frac;
    }

    public double Mean => Samples.Length == 0 ? double.NaN : Samples.Average();
}

/// <summary>Reservoir offset correction (e.g. marine reservoir age), with unit.</summary>
public sealed record ReservoirOffset(
    string Id,
    Quantity Offset,
    string AppliesTo) : Material(Id);

/// <summary>An event layer (tephra, flood, etc.) correlated into the core.</summary>
public sealed record EventLayer(
    string Id,
    string DatumId,
    Quantity Depth,
    string EventName,
    AgeDistribution? CorrelatedAge) : Material(Id);

/// <summary>
/// An exclusion decision. Exclusions are materials too: they are preserved
/// forever, with reason and author, and are part of the export identity.
/// </summary>
public sealed record ExclusionRecord(
    string Id,
    string TargetId,
    string Reason,
    string DecidedBy,
    string DecidedAt) : Material(Id);
