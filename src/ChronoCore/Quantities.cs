using System.Text.Json.Serialization;

namespace ChronoCore;

public enum DiagSeverity { Info, Warning, Error }

/// <summary>带位置的诊断；输入不完整或不可转换时必须返回，不得用空值伪装成功。</summary>
public sealed record Diagnostic(string Code, DiagSeverity Severity, string Message, string? Path = null)
{
    public static Diagnostic Error(string code, string message, string? path = null)
        => new(code, DiagSeverity.Error, message, path);
    public static Diagnostic Warn(string code, string message, string? path = null)
        => new(code, DiagSeverity.Warning, message, path);
    public static Diagnostic Info(string code, string message, string? path = null)
        => new(code, DiagSeverity.Info, message, path);
}

/// <summary>
/// 资源预算耗尽(ResourceExhausted)、格式损坏(FormatCorrupt)、语义无解(SemanticNoSolution)
/// 是三种互不相同的结果，另有正常(Ok)与并发冲突(Conflict)。
/// </summary>
public enum OutcomeKind { Ok, ResourceExhausted, FormatCorrupt, SemanticNoSolution, Conflict, NotFound }

public sealed class Outcome<T>
{
    public OutcomeKind Kind { get; }
    public T? Value { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Ok => Kind == OutcomeKind.Ok;
    private Outcome(OutcomeKind kind, T? value, IReadOnlyList<Diagnostic> diags)
    { Kind = kind; Value = value; Diagnostics = diags; }

    public static Outcome<T> Success(T value) => new(OutcomeKind.Ok, value, Array.Empty<Diagnostic>());
    public static Outcome<T> Fail(OutcomeKind kind, IReadOnlyList<Diagnostic> diags)
        => new(kind, default, diags);
    public static Outcome<T> Fail(OutcomeKind kind, string code, string message, string? path = null)
        => new(kind, default, new[] { new Diagnostic(code, DiagSeverity.Error, message, path) });

    public Outcome<TResult> Map<TResult>(Func<T, TResult> f)
        => Ok ? Outcome<TResult>.Success(f(Value!))
              : Outcome<TResult>.Fail(Kind, Diagnostics);
}

/// <summary>带物理单位、原始表示与坐标/时间基准的量。导入时保留原始表示，派生层才规范化。</summary>
public sealed record Measure(double Value, string Unit, string? Raw = null, string? Basis = null, bool Convertible = true)
{
    public const string DepthM = "m";
    public const string AgeKa = "ka BP";
    public const string RateCmKa = "cm/ka";

    public static Measure Depth(double m, string? raw = null, string? basis = null)
        => new(m, DepthM, raw ?? Format(m, DepthM), basis, true);

    public static Measure Age(double ka, string? raw = null, string? basis = null)
        => new(ka, AgeKa, raw ?? Format(ka, AgeKa), basis, true);

    public static string Format(double v, string unit) => $"{v:0.###} {unit}";
}

/// <summary>深度坐标基准（如 CCSF / CSF-A 深度表）。</summary>
public sealed record DepthDatum(string Id, string Name, string VerticalDatum, string Scale, string Unit,
    double TopM, double BottomM, string? Basis);
