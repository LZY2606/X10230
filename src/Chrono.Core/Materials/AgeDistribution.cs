using System.Text.Json.Serialization;

namespace Chrono.Core.Materials;

public enum DistributionKind { Point, Normal, StudentT, Uniform, Empirical }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PointDistribution), "point")]
[JsonDerivedType(typeof(NormalDistribution), "normal")]
[JsonDerivedType(typeof(StudentTDistribution), "student-t")]
[JsonDerivedType(typeof(UniformDistribution), "uniform")]
[JsonDerivedType(typeof(EmpiricalDistribution), "empirical")]
public abstract record AgeDistribution(DistributionKind Kind)
{
    public abstract double Quantile(double p);
    public abstract double Mean();
    public abstract bool IsLongTail();
}

public sealed record PointDistribution(double AgeCalBp) : AgeDistribution(DistributionKind.Point)
{
    public override double Quantile(double p) => AgeCalBp;
    public override double Mean() => AgeCalBp;
    public override bool IsLongTail() => false;
}

public sealed record NormalDistribution(double MeanCalBp, double SigmaCalYr) : AgeDistribution(DistributionKind.Normal)
{
    public override double Quantile(double p) => MeanCalBp + SigmaCalYr * Statistics.Probit(p);
    public override double Mean() => MeanCalBp;
    public override bool IsLongTail() => false;
}

public sealed record StudentTDistribution(double LocationCalBp, double ScaleCalYr, double DegreesOfFreedom)
    : AgeDistribution(DistributionKind.StudentT)
{
    public override double Quantile(double p) => LocationCalBp + ScaleCalYr * Statistics.StudentTQuantile(p, DegreesOfFreedom);
    public override double Mean() => DegreesOfFreedom > 1 ? LocationCalBp : double.NaN;
    public override bool IsLongTail() => DegreesOfFreedom <= 4;
}

public sealed record UniformDistribution(double LowerCalBp, double UpperCalBp) : AgeDistribution(DistributionKind.Uniform)
{
    public override double Quantile(double p) => LowerCalBp + (UpperCalBp - LowerCalBp) * p;
    public override double Mean() => (LowerCalBp + UpperCalBp) / 2.0;
    public override bool IsLongTail() => false;
}

public sealed record EmpiricalDistribution(IReadOnlyList<double> SortedAgesCalBp)
    : AgeDistribution(DistributionKind.Empirical)
{
    public override double Quantile(double p)
    {
        var n = SortedAgesCalBp.Count;
        if (n == 0) return double.NaN;
        var clamped = Math.Clamp(p, 0, 1);
        var index = (int)Math.Floor(clamped * (n - 1));
        return SortedAgesCalBp[Math.Min(index, n - 1)];
    }

    public override double Mean() => SortedAgesCalBp.Average();
    public override bool IsLongTail()
    {
        if (SortedAgesCalBp.Count < 8) return false;
        var median = Statistics.Median(SortedAgesCalBp);
        var upperTail = SortedAgesCalBp[^1] - median;
        var iqr = Statistics.Median(SortedAgesCalBp.Take(SortedAgesCalBp.Count * 3 / 4).ToList())
                  - Statistics.Median(SortedAgesCalBp.Take(SortedAgesCalBp.Count / 4 + 1).ToList());
        return iqr > 0 && upperTail > 3.0 * iqr;
    }
}
