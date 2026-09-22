namespace AgeDepth.Core;

/// <summary>Three failure kinds must stay distinguishable end to end.</summary>
public enum FailureKind { None, ResourceBudgetExhausted, FormatCorrupt, SemanticUnsolvable }

/// <summary>A diagnostic always carries a position so nothing fails silently.</summary>
public sealed record Diagnostic(string Code, string Message, string Position);

/// <summary>Machine-readable judgment basis for a domain rule, stored with results.</summary>
public sealed record RuleEvent(string Rule, string Subject, string Detail);

public sealed class ComputeResult
{
    public FailureKind Failure { get; set; } = FailureKind.None;
    public ModelOutput? Output { get; set; }
    public List<Diagnostic> Diagnostics { get; } = new();
    public static ComputeResult Failed(FailureKind kind, string code, string message, string position)
    {
        var r = new ComputeResult { Failure = kind };
        r.Diagnostics.Add(new Diagnostic(code, message, position));
        return r;
    }
}

public static class Units
{
    /// <summary>Depth conversion to metres. Returns null when the unit is unknown.</summary>
    public static double? DepthToMetres(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "m" or "metre" or "metres" or "meter" or "meters" => 1.0,
        "cm" => 0.01,
        "ft" or "feet" or "foot" => 0.3048,
        _ => null
    };

    /// <summary>Age conversion to ka (thousand years). Returns null when unknown.</summary>
    public static double? AgeToKa(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "ka" or "kyr" => 1.0,
        "ma" => 1000.0,
        "yr" or "a" or "years" => 0.001,
        _ => null
    };
}

public sealed record EngineParams(string Version, int Draws, int MaxAnchors, int MaxCandidates)
{
    public static readonly EngineParams Default = new("1.0", 256, 400, 16);
}

/// <summary>Anchor ages arrive as a full sample distribution; a bare mean is rejected upstream.</summary>
public sealed record AnchorInput(
    string Id, double Depth, string DepthUnit, string Datum,
    double[] Samples, string AgeUnit,
    double? ReservoirOffset, string? ReservoirUnit, string Raw);

/// <summary>Boundary describes how this segment joins the one above: start | gap | unconformity.</summary>
public sealed record SegmentInput(string Id, double Top, double Base, string Unit, string Datum, string Boundary);

public sealed record EventLayerInput(string Id, double Depth, string Unit, string Datum, string Name, string CorrelatesTo);

public sealed record ModelInput(
    IReadOnlyList<SegmentInput> Segments,
    IReadOnlyList<AnchorInput> Anchors,
    IReadOnlyList<EventLayerInput> EventLayers,
    IReadOnlyList<string> ExcludedAnchorIds);

public sealed record RateInterval(
    string FromAnchor, string ToAnchor, double TopDepth, double BaseDepth,
    double MinRate, double MaxRate, string Unit);

public sealed record CandidateModel(
    string Id, IReadOnlyList<string> AnchorIds, bool Equiprobable,
    double[] Depths, double[] P5, double[] P50, double[] P95,
    IReadOnlyList<RateInterval> Rates);

public sealed record SegmentModel(
    string SegmentId, string Status,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ModelOutput(
    string InputDigest, string ParamVersion,
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<RuleEvent> RuleEvents);
