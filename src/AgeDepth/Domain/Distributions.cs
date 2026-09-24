namespace AgeDepth.Domain;

/// <summary>
/// A radiometric age distribution. The engine propagates quantiles and never
/// substitutes the mean for the distribution. Long tails are kept via P999.
/// </summary>
public sealed record AgeDistribution
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public required string Kind { get; init; } // normal | longtail | uniform
    public required double Median { get; init; }
    public required double P025 { get; init; }
    public required double P975 { get; init; }
    public double? P999 { get; init; }
    public double? Mean { get; init; }

    public AgeDistribution Shift(double years) => this with
    {
        Median = Median + years,
        P025 = P025 + years,
        P975 = P975 + years,
        P999 = P999 is null ? null : P999 + years,
        Mean = Mean is null ? null : Mean + years,
    };
}
