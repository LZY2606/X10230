using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Core;

/// <summary>Location-tagged diagnostic. Unconvertible values stay raw in place and carry one of these.</summary>
public record Diagnostic(string Code, string Message, string Location);

/// <summary>Distinct failure taxonomy: budget, format and semantic failures are never conflated.</summary>
public enum FailureKind { None, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

/// <summary>A quantity as imported: raw text plus unit, preserved verbatim from the source.</summary>
public record RawQuantity(string Raw, string Unit)
{
    public static RawQuantity Of(double v, string unit) => new(v.ToString("G17"), unit);
}

public static class Units
{
    // Normalization targets: depth -> metre, age -> ka (thousand years BP).
    public static bool TryNormalizeDepth(RawQuantity q, out double metres, out string? error)
    {
        metres = 0; error = null;
        if (!double.TryParse(q.Raw, out var v)) { error = $"not a number: '{q.Raw}'"; return false; }
        switch (q.Unit.Trim().ToLowerInvariant())
        {
            case "m" or "mbsf" or "meter" or "metre": metres = v; return true;
            case "cm": metres = v / 100.0; return true;
            case "ft": metres = v * 0.3048; return true;
            default: error = $"unknown depth unit '{q.Unit}'"; return false;
        }
    }

    public static bool TryNormalizeAge(RawQuantity q, out double ka, out string? error)
    {
        ka = 0; error = null;
        if (!double.TryParse(q.Raw, out var v)) { error = $"not a number: '{q.Raw}'"; return false; }
        switch (q.Unit.Trim().ToLowerInvariant())
        {
            case "ka" or "ka bp": ka = v; return true;
            case "yr bp" or "cal yr bp" or "a bp": ka = v / 1000.0; return true;
            case "ma": ka = v * 1000.0; return true;
            default: error = $"unknown age unit '{q.Unit}'"; return false;
        }
    }
}

public static class StableId
{
    public static string Of(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}_{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public static string DigestOf<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}

// ---------- Immutable material layer ----------

public record DepthDatum(string Id, string Name, string Reference);

public enum BoundaryAfter { None, DepthGap, Unconformity }

public record CoreSegment(
    string Id,
    string DatumId,
    RawQuantity Top,
    RawQuantity Bottom,
    BoundaryAfter BoundaryAfter,
    string Source);

public record AgeAnchor(
    string Id,
    string SegmentId,
    RawQuantity Depth,
    string AgeUnit,
    IReadOnlyList<string> AgeSamplesRaw,   // full radiometric distribution, never collapsed to a mean
    RawQuantity? ReservoirOffset,          // ΔR with its own distribution/uncertainty
    string? EventLayerId,
    string Source);

public record EventLayer(
    string Id,
    string Name,
    RawQuantity? Age,                      // may be null: correlated-only layer
    IReadOnlyList<string> CorrelatedAnchorIds,
    string Source);

// ---------- Modifiable decision layer ----------

public record ExclusionDecision(
    string Id,
    string AnchorId,
    string Reason,
    bool Adopted,          // both adopted and rejected decisions are retained
    string DecidedBy);

public record ParameterSet(
    string Id,
    string RuleVersion,
    int Draws,
    int SampleBudget,
    double MonotonicToleranceKa,
    bool ApplyReservoirCorrection);

// ---------- Derived model layer ----------

public record DepthNode(string Id, double DepthM, double P05, double P50, double P95, int Draws);

public record RateInterval(
    string Id,
    double FromDepthM,
    double ToDepthM,
    double RateP05,
    double RateP50,
    double RateP95,
    string Unit);          // "cm/ka"

public record RunModel(
    string RunId,
    IReadOnlyList<string> SegmentIds,
    IReadOnlyList<DepthNode> Nodes,
    IReadOnlyList<RateInterval> Rates,
    string RunInputDigest);

public record CandidateModel(
    string Id,
    IReadOnlyList<string> KeptAnchorIds,
    IReadOnlyList<string> ExcludedAnchorIds,
    IReadOnlyList<RunModel> Runs,
    string Rationale);

public record BuildParameters(
    string RuleVersion,
    int Draws,
    int SampleBudget,
    double MonotonicToleranceKa);

public record BuildOutput(
    FailureKind Failure,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<JudgmentEvent> Judgments,
    string InputDigest);
