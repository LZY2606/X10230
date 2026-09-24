namespace AgeDepth.Engine;

/// <summary>Versioned computation parameters.</summary>
public sealed record ModelParameters
{
    public string Version { get; init; } = "params-v1";
    public int EnsembleSize { get; init; } = 200;
    public int MaxIterations { get; init; } = 20_000;
    public int Seed { get; init; } = 20260924;
    /// <summary>Candidates within this roughness multiple of the best score are co-optimal.</summary>
    public double EqualTolerance { get; init; } = 1.05;
    /// <summary>Band percentiles emitted for the UI.</summary>
    public double BandLower { get; init; } = 0.025;
    public double BandUpper { get; init; } = 0.975;
}
