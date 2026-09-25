using System.Text.Json.Nodes;

namespace AgeDepth.Core;

public enum MaterialKind { DepthDatum, CoreSection, Boundary, AgeAnchor, EventLayer }

/// <summary>A structured, machine-readable judgment or problem report. Never a log line.</summary>
public sealed record Diagnostic(
    string Code,       // e.g. DEPTH_DUPLICATE, UNIT_UNCONVERTIBLE, INVERTED_SEGMENT
    string Severity,   // info | warning | error
    string EntityId,   // stable id of the affected material/segment/candidate
    string Field,      // which field triggered it
    int? Position,     // index inside the originating import batch
    string Message,
    string? RawValue); // the original representation, preserved

/// <summary>
/// Immutable imported material. Raw representation is always preserved;
/// the normalized view is derived and may be null when not convertible.
/// </summary>
public sealed record Material(
    string Id,
    MaterialKind Kind,
    string SourceKey,
    JsonObject Raw,
    JsonObject? Norm,
    string BatchId,
    long Seq);

public sealed record Decision(
    string Id,
    string Kind,        // exclude-anchor | accept-candidate | reject-candidate | revise-param | supersede-material | note
    string Target,
    JsonObject Payload,
    string? Supersedes,
    long Seq);

public sealed record AgeEvent(
    long Seq,
    string Id,
    string Type,        // import | compute | freeze | decision | export | import-archive | recovery | counterexample
    JsonObject Payload,
    string? InputDigest,
    string? ParamVersion,
    string[] Parents,
    string CreatedUtc);

public sealed record FreezeRecord(
    string CandidateSetId,
    string[] CandidateIds,
    string ParamVersion,
    long Seq);

/// <summary>Three failure classes must stay distinguishable from success.</summary>
public enum ComputeStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

public sealed record ComputeParams(
    int MaxCandidates = 64,
    int Draws = 256,
    int GridPoints = 41,
    double FitTolerance = 1e-6,
    double ColocatedEpsilonM = 0.005,
    string Version = "1.0.0")
{
    public string Digest() => Ids.DigestText($"{MaxCandidates}|{Draws}|{GridPoints}|{FitTolerance:R}|{ColocatedEpsilonM:R}|{Version}");
}

public sealed record RateInterval(
    string FromAnchorId, string ToAnchorId,
    double TopDepthM, double BottomDepthM,
    double RateCmPerKaMedian, double RateCmPerKaP05, double RateCmPerKaP95);

public sealed record AgeDepthBand(
    double[] DepthsM, double[] MedianYrBp, double[] P05YrBp, double[] P95YrBp,
    double[] P16YrBp, double[] P84YrBp);

public sealed record Candidate(
    string Id,
    string SegmentId,
    string[] ControlAnchorIds,
    string[] ExcludedAnchorIds,
    string? ParentId,
    string BranchReason,   // base | inverted-exclusion | colocated-choice | ...
    double Score,
    bool Admissible,
    AgeDepthBand Band,
    RateInterval[] Rates,
    string InputDigest,
    string ParamVersion);

public sealed record Segment(
    string Id,
    double TopM,
    double BottomM,
    string BoundaryAbove,   // none | gap | unconformity | fault | top
    string BoundaryBelow,
    string[] AnchorIds);

public sealed record ComputeResult(
    ComputeStatus Status,
    bool Partial,
    List<Candidate> Candidates,
    List<Segment> Segments,
    List<Diagnostic> Diagnostics,
    string InputDigest,
    string ParamVersion);

public sealed record ImportResult(
    ComputeStatus Status,
    string BatchId,
    List<Material> Materials,
    List<Diagnostic> Diagnostics,
    long Seq);
