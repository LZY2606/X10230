using ChronoCore.Quantities;

namespace ChronoCore.Domain;

/// <summary>
/// A radiometric / dated anchor. The whole age distribution is material and must
/// never be silently replaced by its mean. Raw representation is preserved; only
/// the derived layer normalises units.
/// </summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string SectionId { get; init; }
    public required Measure Depth { get; init; }

    /// <summary>Raw reported age, exactly as imported (e.g. "3120 14c yr bp").</summary>
    public required string RawAge { get; init; }
    /// <summary>Parsed age in its reported unit; may carry conversion diagnostics.</summary>
    public required Measure ReportedAge { get; init; }
    /// <summary>Time scale label kept verbatim (cal yr BP, 14C yr BP ...).</summary>
    public required string TimeBasis { get; init; }
    /// <summary>Calendar reference year (e.g. 1950 for BP).</summary>
    public int ReferenceYear { get; init; } = 1950;

    /// <summary>Full distribution material. Never reduced to a scalar mean.</summary>
    public required AgeDistribution Distribution { get; init; }
    /// <summary>Identifier of the reservoir offset applied on the derived layer (nullable).</summary>
    public string? ReservoirOffsetId { get; init; }
    /// <summary>Id of the event layer this anchor dates, when it dates an event.</summary>
    public string? EventLayerId { get; init; }
    public string? LabCode { get; init; }
    public string? Material { get; init; }
    public int Revision { get; init; } = 1;
    public string? Supersedes { get; init; }
    public string? SourceFragment { get; init; }
}

/// <summary>Tagged distribution union. Serialised as {type, ...}.</summary>
public abstract record AgeDistribution
{
    public abstract string Type { get; }
}

public sealed record NormalDistribution : AgeDistribution
{
    public override string Type => "normal";
    public required double Mean { get; init; }
    public required double Sigma { get; init; }
}

public sealed record StudentTDistribution : AgeDistribution
{
    public override string Type => "student_t";
    public required double Location { get; init; }
    public required double Scale { get; init; }
    /// <summary>Degrees of freedom; small df = long tail.</summary>
    public required double Df { get; init; }
}

/// <summary>Two-component mixture: a thin/long contamination tail is explicit.</summary>
public sealed record MixtureDistribution : AgeDistribution
{
    public override string Type => "mixture";
    public required double WeightLong { get; init; }
    public required NormalDistribution Core { get; init; }
    public required NormalDistribution LongTail { get; init; }
}

/// <summary>Empirical sampled distribution; samples are the material.</summary>
public sealed record EmpiricalDistribution : AgeDistribution
{
    public override string Type => "empirical";
    public required IReadOnlyList<double> Samples { get; init; }
}
