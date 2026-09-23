using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth;

/// <summary>三种必须可区分的失败，外加并发冲突。</summary>
public enum FailureKind { FormatCorrupted, SemanticUnsolvable, BudgetExhausted, Conflict }

public sealed record Failure(FailureKind Kind, string Message, string? Position)
{
    public override string ToString() => $"{Kind}: {Message}" + (Position is null ? "" : $" (at {Position})");
}

public sealed class Outcome<T>
{
    public T? Value { get; }
    public Failure? Error { get; }
    public bool Ok => Error is null;
    private Outcome(T? value, Failure? error) { Value = value; Error = error; }
    public static Outcome<T> Success(T value) => new(value, null);
    public static Outcome<T> Fail(FailureKind kind, string message, string? position = null) =>
        new(default, new Failure(kind, message, position));
    public T Unwrap() => Ok ? Value! : throw new InvalidOperationException(Error!.ToString());
}

public sealed class Outcome
{
    public Failure? Error { get; }
    public bool Ok => Error is null;
    private Outcome(Failure? error) { Error = error; }
    public static readonly Outcome Success = new(null);
    public static Outcome Fail(FailureKind kind, string message, string? position = null) =>
        new(new Failure(kind, message, position));
}

/// <summary>领域量：永远携带单位与坐标/时间基准，并保留导入时的原始表示。</summary>
public sealed record Quantity(double Value, string Unit, string Datum, string Raw)
{
    public static Quantity Of(double value, string unit, string datum) =>
        new(value, unit, datum, $"{value} {unit} [{datum}]");
}

/// <summary>年龄分布：样本永远保留，绝不用均值替代。</summary>
public sealed record AgeDist(
    string Kind,                 // samples | normal | uniform | point
    double[]? Samples,           // 原始样本（长尾分布直接保留）
    double? Mean, double? Sigma, // normal
    double? Min, double? Max,    // uniform
    string Unit, string Datum)
{
    public double[] EffectiveSamples() => Kind switch
    {
        "samples" => Samples ?? Array.Empty<double>(),
        "point" => new[] { Mean ?? 0 },
        "normal" => Enumerable.Range(0, 64)
            .Select(i => (Mean ?? 0) + (Sigma ?? 0) * NormalQuantile((i + 0.5) / 64.0)).ToArray(),
        "uniform" => Enumerable.Range(0, 64)
            .Select(i => (Min ?? 0) + ((Max ?? 0) - (Min ?? 0)) * (i + 0.5) / 64.0).ToArray(),
        _ => Array.Empty<double>()
    };

    private static double NormalQuantile(double p) // Acklam 近似
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double pl = 0.02425;
        if (p < pl) { double q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        if (p > 1 - pl) { double q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        double qq = p - 0.5, r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    public double Quantile(double p)
    {
        var s = EffectiveSamples();
        if (s.Length == 0) return double.NaN;
        Array.Sort(s);
        double idx = p * (s.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }
    public double Median => Quantile(0.5);
    public double Q05 => Quantile(0.05);
    public double Q95 => Quantile(0.95);
}

// ---------- 不可变材料 ----------
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CoreSectionMat), "coreSection")]
[JsonDerivedType(typeof(DepthGapMat), "depthGap")]
[JsonDerivedType(typeof(UnconformityMat), "unconformity")]
[JsonDerivedType(typeof(AgeAnchorMat), "ageAnchor")]
[JsonDerivedType(typeof(EventLayerMat), "eventLayer")]
public abstract record Material(string Id)
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed record CoreSectionMat(string Id, string Label, Quantity Top, Quantity Bottom) : Material(Id)
{ [JsonIgnore] public override string Kind => "coreSection"; }

public sealed record DepthGapMat(string Id, Quantity Top, Quantity Bottom, string Reason) : Material(Id)
{ [JsonIgnore] public override string Kind => "depthGap"; }

public sealed record UnconformityMat(string Id, Quantity Depth, Quantity? Hiatus) : Material(Id)
{ [JsonIgnore] public override string Kind => "unconformity"; }

public sealed record AgeAnchorMat(string Id, string Label, Quantity Depth, AgeDist Dist, Quantity? ReservoirOffset) : Material(Id)
{ [JsonIgnore] public override string Kind => "ageAnchor"; }

public sealed record EventLayerMat(string Id, string Name, Quantity Depth, string CorrelatesTo) : Material(Id)
{ [JsonIgnore] public override string Kind => "eventLayer"; }

// ---------- 可修改决定 ----------
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExclusionDecision), "exclusion")]
[JsonDerivedType(typeof(CandidateVerdict), "candidateVerdict")]
[JsonDerivedType(typeof(ParamRevision), "paramRevision")]
public abstract record Decision(string Id, string? Supersedes)
{
    [JsonIgnore] public abstract string Kind { get; }
}

public sealed record ExclusionDecision(string Id, string TargetMaterialId, string Reason, bool Active, string? Supersedes = null)
    : Decision(Id, Supersedes)
{ [JsonIgnore] public override string Kind => "exclusion"; }

public sealed record CandidateVerdict(string Id, string CandidateId, bool Accepted, string Reason, string? Supersedes = null)
    : Decision(Id, Supersedes)
{ [JsonIgnore] public override string Kind => "candidateVerdict"; }

public sealed record ParamRevision(string Id, string ParamsVersion, double MonotonicityToleranceKa, int CandidateBudget, string Reason, string? Supersedes = null)
    : Decision(Id, Supersedes)
{ [JsonIgnore] public override string Kind => "paramRevision"; }

// ---------- 派生层 ----------
public sealed record Diagnostic(string Code, string Message, string Position, string Severity);

public sealed record NormAnchor(string Id, string ParentId, double DepthM, string DepthDatum,
    double[] SamplesKa, string AgeDatum, double ReservoirOffsetKa, string RawDepth, string RawDist);

public sealed record Segment(string Id, int Index, double TopM, double BottomM,
    string TopBoundary, string BottomBoundary, List<string> AnchorIds, List<string> EventLayerIds);

public sealed record RateInterval(double TopM, double BottomM, double MinMPerKa, double MaxMPerKa);

public sealed record BandPoint(double DepthM, double Q05, double Median, double Q95);

public sealed record Candidate(
    string Id, int SegmentIndex, string Status, string? RejectReason,
    List<BandPoint> Band, List<RateInterval> Rates,
    List<string> Parents, string ParamsVersion, string InputDigest, double Quality);

public sealed record RebuildReport(
    List<NormAnchor> Anchors, List<Diagnostic> Diagnostics,
    List<Segment> Segments, List<Candidate> Candidates,
    string ParamsVersion, string InputDigest);

public sealed record FrozenModel(string Id, string Name, string CandidateId, int Version, bool Frozen,
    List<BandPoint> Band, string InputDigest, string ParamsVersion, long FrozenAtSeq);

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Ser<T>(T v) => JsonSerializer.Serialize(v, Opts);
    public static T? De<T>(string s) => JsonSerializer.Deserialize<T>(s, Opts);
}
