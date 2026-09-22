using System.Text.Json;
using StratAge.Core.Domain;

namespace StratAge.Core.Engine;

public enum DerivationStatus { Ok, SemanticImpossible, BudgetExhausted }

public enum DiagnosticLevel { Info, Warning, Error }

public sealed record Diagnostic(string Code, DiagnosticLevel Level, string Message, SourcePos? Pos,
    IReadOnlyList<string> AffectedIds)
{
    public static Diagnostic Err(string code, string msg, SourcePos? pos, params string[] ids)
        => new(code, DiagnosticLevel.Error, msg, pos, ids);
    public static Diagnostic Warn(string code, string msg, SourcePos? pos, params string[] ids)
        => new(code, DiagnosticLevel.Warning, msg, pos, ids);
    public static Diagnostic Info(string code, string msg, SourcePos? pos, params string[] ids)
        => new(code, DiagnosticLevel.Info, msg, pos, ids);
}

/// <summary>An anchor after reservoir correction and exclusion projection. Full distribution kept.</summary>
public sealed record ResolvedAnchor(
    string AnchorId, double DepthMbsf, QValue RawDepth, QValue RawAge,
    double[] AgeDraws,        // corrected, canonical ka BP; one draw per Monte Carlo replicate
    double MedianKa, double P025Ka, double P975Ka,
    double? RawMeanKa,        // informational only - never used as the anchor age
    string? ReservoirOffsetId, bool Excluded, string? ExclusionReason, bool InvertedSection,
    int SectionIndex, int TieCluster, int TieRank);

public sealed record SectionModel(int Index, double TopMbsf, double BottomMbsf, bool IsGap,
    string? BoundaryReason, IReadOnlyList<ResolvedAnchor> Anchors);

/// <summary>A grid point on the depth-age curve with its propagated uncertainty band.</summary>
public sealed record CurvePoint(string StableId, double DepthMbsf, double MedianKa,
    double LowKa, double HighKa, int SectionIndex);

/// <summary>Sedimentation rate interval (P025..P975) for one section subinterval.</summary>
public sealed record RateInterval(string StableId, double FromMbsf, double ToMbsf,
    double MedianMPerKa, double LowMPerKa, double HighMPerKa, int SectionIndex);

public sealed class CandidateModel
{
    public required string StableId { get; init; }
    public required string Method { get; init; }
    public required string MethodLabel { get; init; }
    public required string TieSignature { get; init; }
    public required IReadOnlyList<CurvePoint> Curve { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required bool Feasible { get; init; }
    public double BandWidthKa { get; init; }
    public double FitPenalty { get; init; }
    public double Score { get; init; }
    public bool ParetoOptimal { get; set; }
    public bool EquallyOptimal { get; set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
    public bool Frozen { get; set; }
    public string? FrozenByEventId { get; set; }
}

public sealed class ModelDerivation
{
    public required DerivationStatus Status { get; init; }
    public required MaterialSet Material { get; init; }
    public required RuleSetVersion Rule { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<ResolvedAnchor> Anchors { get; init; }
    public required IReadOnlyList<SectionModel> Sections { get; init; }
    public required IReadOnlyList<CandidateModel> Candidates { get; init; }
    public required int BudgetUsed { get; init; }
    public required int BudgetLimit { get; init; }
    public required string InputDigest { get; init; }

    public IEnumerable<CandidateModel> FeasibleCandidates => Candidates.Where(c => c.Feasible);
}
