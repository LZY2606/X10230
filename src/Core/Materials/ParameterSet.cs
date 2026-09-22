namespace AgeModel.Core.Materials;

/// <summary>
/// Versioned derivation parameters. Every derived result records which parameter set
/// (hence rule version) produced it.
/// </summary>
public sealed record ParameterSet
{
    public required string Id { get; init; }
    public required string RulesVersion { get; init; }
    public int OrderIndex { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Calendar reference datum for radiocarbon results, e.g. "BP1950".</summary>
    public string CalendarDatum { get; init; } = "BP1950";
    /// <summary>Monte-Carlo draws for uncertainty propagation.</summary>
    public int McSamples { get; init; } = 4000;
    public ulong McSeed { get; init; } = 0xA6E_0001UL;
    /// <summary>P(younger-at-deeper &gt; this threshold) ⇒ monotonic violation inside one segment.</summary>
    public double InversionProbabilityThreshold { get; init; } = 0.10;
    /// <summary>Depth grid spacing for the plotted uncertainty band (canonical metres).</summary>
    public double GridSpacingM { get; init; } = 0.1;
    /// <summary>Hard cap on segment candidate configurations enumerated.</summary>
    public int CandidateBudget { get; init; } = 512;
    /// <summary>Apply reservoir offset R+ΔR subtraction before model fitting.</summary>
    public bool ApplyReservoirCorrection { get; init; } = true;
    /// <summary>Policy when inversion is detected: "reject-offender" excludes offending anchors; "strict" fails the segment.</summary>
    public string InversionPolicy { get; init; } = "reject-offender";

    public ParameterSet WithId(string id, int order, string name) => this with
    {
        Id = id, OrderIndex = order, Name = name
    };

    public static ParameterSet Baseline(int order = 0) => new()
    {
        Id = "params-v1", RulesVersion = "chron-rules-1.0.0", OrderIndex = order,
        Name = "基线参数 (chron-rules 1.0.0)"
    };
}
