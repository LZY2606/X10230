using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth;

/// <summary>Failure categories are deliberately distinct: resource budget, corrupt format, semantic unsolvability, optimistic-concurrency conflict.</summary>
public enum FailureKind { None, BudgetExhausted, CorruptFormat, Unsolvable, Conflict }

public sealed record Diagnostic(string Code, string Message, string Position, string ObjectId)
{
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JsonOpts.Default);
}

public sealed class Outcome<T>
{
    public T? Value { get; }
    public FailureKind Failure { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsOk => Failure == FailureKind.None;

    private Outcome(T? value, FailureKind failure, IReadOnlyList<Diagnostic> diagnostics)
    { Value = value; Failure = failure; Diagnostics = diagnostics; }

    public static Outcome<T> Ok(T value, IReadOnlyList<Diagnostic>? diags = null) =>
        new(value, FailureKind.None, diags ?? Array.Empty<Diagnostic>());

    public static Outcome<T> Fail(FailureKind kind, params Diagnostic[] diags) =>
        new(default, kind, diags);

    public static Outcome<T> Fail(FailureKind kind, IReadOnlyList<Diagnostic> diags) =>
        new(default, kind, diags);
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

public static class Hashing
{
    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}

/// <summary>A measured quantity that always keeps its raw textual representation, unit and coordinate datum.</summary>
public sealed record Qty(double Value, string Unit, string Datum, string Raw)
{
    public string Display => $"{Raw} [{Unit} · {Datum}]";
}

/// <summary>Age distribution of an anchor. Never collapsed to a mean: median and sampling both use the full shape.</summary>
public sealed record Dist(string Kind, double P1, double P2, string Raw)
{
    public double Median() => Kind switch
    {
        "normal" => P1,
        "uniform" => (P1 + P2) / 2.0,
        "longtail" => Math.Exp(P1), // lognormal(mu, sigma): median = e^mu, mean = e^(mu+sigma^2/2)
        "point" => P1,
        _ => P1
    };

    public double Mean() => Kind switch
    {
        "normal" => P1,
        "uniform" => (P1 + P2) / 2.0,
        "longtail" => Math.Exp(P1 + P2 * P2 / 2.0),
        "point" => P1,
        _ => P1
    };

    public double Sample(Random rng) => Kind switch
    {
        "normal" => P1 + P2 * NextGaussian(rng),
        "uniform" => P1 + (P2 - P1) * rng.NextDouble(),
        "longtail" => Math.Exp(P1 + P2 * NextGaussian(rng)),
        "point" => P1,
        _ => P1
    };

    public double Pdf(double x) => Kind switch
    {
        "normal" => Math.Exp(-0.5 * Math.Pow((x - P1) / P2, 2)) / (P2 * Math.Sqrt(2 * Math.PI)),
        "uniform" => (x >= P1 && x <= P2 && P2 > P1) ? 1.0 / (P2 - P1) : 0.0,
        "longtail" => x > 0 ? Math.Exp(-0.5 * Math.Pow((Math.Log(x) - P1) / P2, 2)) / (x * P2 * Math.Sqrt(2 * Math.PI)) : 0.0,
        "point" => 0.0,
        _ => 0.0
    };

    public (double Lo, double Hi) Interval95() => Kind switch
    {
        "normal" => (P1 - 1.96 * P2, P1 + 1.96 * P2),
        "uniform" => (P1, P2),
        "longtail" => (Math.Exp(P1 - 1.96 * P2), Math.Exp(P1 + 1.96 * P2)),
        "point" => (P1, P1),
        _ => (P1, P1)
    };

    public static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>Unit normalisation. Values that cannot be converted stay in raw form and get a diagnostic; nothing is dropped for plotting.</summary>
public static class Units
{
    private static readonly Dictionary<string, double> DepthToMeters = new(StringComparer.OrdinalIgnoreCase)
    { ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048 };

    private static readonly Dictionary<string, double> AgeToKa = new(StringComparer.OrdinalIgnoreCase)
    { ["ka BP"] = 1.0, ["cal ka BP"] = 1.0, ["yr BP"] = 0.001, ["cal yr BP"] = 0.001 };

    public static bool IsDepthUnit(string unit) => DepthToMeters.ContainsKey(unit);
    public static bool IsAgeUnit(string unit) => AgeToKa.ContainsKey(unit);

    public static bool TryDepthMeters(Qty q, out double meters)
    {
        if (DepthToMeters.TryGetValue(q.Unit, out var f)) { meters = q.Value * f; return true; }
        meters = double.NaN; return false;
    }

    public static bool TryAgeKa(Qty q, out double ka)
    {
        if (AgeToKa.TryGetValue(q.Unit, out var f)) { ka = q.Value * f; return true; }
        ka = double.NaN; return false;
    }
}
