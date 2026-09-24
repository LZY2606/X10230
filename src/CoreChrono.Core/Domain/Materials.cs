using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreChrono.Core.Domain;

/// <summary>
/// 锚点年龄分布。分布永远以完整形式参与计算，禁止用均值替代。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "dist")]
[JsonDerivedType(typeof(NormalAge), "normal")]
[JsonDerivedType(typeof(UniformAge), "uniform")]
[JsonDerivedType(typeof(LongTailAge), "longtail")]
public abstract record AgeDistribution(AgeUnit Unit, string Scale)
{
    protected double ToCal(double v) => AgeUnits.ToCalYrBP(v, Unit);
    public abstract double SampleCalYrBP(DeterministicRandom rng);
    public abstract double MedianCalYrBP { get; }
    public abstract double SigmaCalYrBP { get; }
    /// <summary>长尾一侧（偏老方向）的 p95 与中位数距离；用于前端显示分布形态。</summary>
    public abstract double UpperTailCalYrBP { get; }
    public abstract double LowerTailCalYrBP { get; }
    public abstract string Describe();
}

public sealed record NormalAge(double Mean, double Sigma, AgeUnit Unit, string Scale) : AgeDistribution(Unit, Scale)
{
    public override double SampleCalYrBP(DeterministicRandom rng) => ToCal(Mean + Sigma * rng.NextStandardNormal());
    public override double MedianCalYrBP => ToCal(Mean);
    public override double SigmaCalYrBP => ToCal(Sigma);
    public override double UpperTailCalYrBP => ToCal(Sigma) * 1.6449;
    public override double LowerTailCalYrBP => ToCal(Sigma) * 1.6449;
    public override string Describe() => $"N({Mean}±{Sigma} {AgeUnits.Symbol(Unit)})";
}

public sealed record UniformAge(double Min, double Max, AgeUnit Unit, string Scale) : AgeDistribution(Unit, Scale)
{
    public override double SampleCalYrBP(DeterministicRandom rng) => ToCal(Min + (Max - Min) * rng.NextDouble());
    public override double MedianCalYrBP => ToCal((Min + Max) / 2.0);
    public override double SigmaCalYrBP => ToCal((Max - Min) / Math.Sqrt(12.0));
    public override double UpperTailCalYrBP => ToCal(Max - (Min + Max) / 2.0);
    public override double LowerTailCalYrBP => ToCal((Min + Max) / 2.0 - Min);
    public override string Describe() => $"U({Min}..{Max} {AgeUnits.Symbol(Unit)})";
}

/// <summary>带偏老长尾的混合分布（如再搬运/有限灵敏度的 OSL 或碳样）。均值会被长尾拖偏。</summary>
public sealed record LongTailAge(double Mode, double Sigma, double TailLambda, AgeUnit Unit, string Scale) : AgeDistribution(Unit, Scale)
{
    public override double SampleCalYrBP(DeterministicRandom rng)
    {
        var z = rng.NextStandardNormal();
        if (rng.NextDouble() < 0.85) return ToCal(Mode + Sigma * z);
        // 15% 概率进入偏老指数尾
        var tail = -Math.Log(Math.Max(rng.NextDouble(), 1e-12)) * TailLambda;
        return ToCal(Mode) + ToCal(Sigma) * Math.Abs(z) + ToCal(TailLambda) * tail / Math.Max(TailLambda, 1.0);
    }
    public override double MedianCalYrBP => ToCal(Mode) + 0.15 * ToCal(TailLambda) * 0.3;
    public override double SigmaCalYrBP => ToCal(Sigma) + 0.15 * ToCal(TailLambda);
    public override double UpperTailCalYrBP
    {
        get
        {
            var rng = new DeterministicRandom(0xC0FFEE);
            var s = Enumerable.Range(0, 2000).Select(_ => SampleCalYrBP(rng)).OrderBy(x => x).ToList();
            return s[(int)(0.95 * (s.Count - 1))] - MedianCalYrBP;
        }
    }
    public override double LowerTailCalYrBP => ToCal(Sigma) * 1.6449;
    public override string Describe() => $"LongTail(mode {Mode}, σ {Sigma}, λ {TailLambda} {AgeUnits.Symbol(Unit)})";
}

// ---- 不可变材料：导入时保留原始表示，派生层才规范化；无法转换的值留在原位置并附诊断 ----

public sealed record DepthDatumPoint(
    string Id, string Label, string RawDepth, DepthValue? Depth, string DatumDefinition, string Note, string SourceRef);

public sealed record SegmentGap(
    string Id, string RawTop, string RawBottom, DepthValue? Top, DepthValue? Bottom,
    string GapKind, string Reason, string SourceRef);

public sealed record RadiometricAge(
    string Id, string LabCode, string Method, string MaterialDated,
    string RawDepth, DepthValue? Depth, string RawAgeJson, AgeDistribution? Age, string SourceRef);

public sealed record ReservoirOffset(
    string Id, string AppliesToMethod, string AppliesToMaterial,
    string RawOffset, AgeValue? Offset, string Rationale, string SourceRef);

public sealed record EventBed(
    string Id, string Name, string RawDepth, DepthValue? Depth,
    string? CorrelatesTo, string? RawCorrelationAgeJson, AgeDistribution? CorrelationAge, string SourceRef);

public sealed record ExclusionDecision(
    string Id, string Target, string Decision, string Reason, string? Supersedes);

/// <summary>一条已导入材料：原始 JSON、内容哈希（不可变身份）、规范化载荷与诊断。</summary>
public sealed record ImportedMaterial(
    string Id, string Kind, string RawJson, string ContentHash,
    JsonElement Payload, IReadOnlyList<Diagnostic> Diagnostics)
{
    public T? As<T>() => JsonSerializer.Deserialize<T>(Payload.GetRawText());
}
