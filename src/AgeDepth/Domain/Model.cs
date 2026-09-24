namespace AgeDepth.Domain;

public sealed record Diagnostic(string Code, string Position, string Message, string? SubjectId);

/// <summary>Machine-readable judgment emitted by the engine.</summary>
public sealed record Judgment(string Rule, string Subject, string Evidence);

public sealed record AgeDepthPoint(double DepthM, double AgeYr);

public sealed record RateInterval(
    string SegmentId,
    double FromM,
    double ToM,
    double RateCmPerKyr,
    double MinCmPerKyr,
    double MaxCmPerKyr);

public sealed record CandidateModel(
    string Id,
    string SegmentId,
    IReadOnlyList<string> AdoptedAnchors,
    IReadOnlyList<string> ExcludedAnchors,
    IReadOnlyList<AgeDepthPoint> Median,
    IReadOnlyList<AgeDepthPoint> Lower,
    IReadOnlyList<AgeDepthPoint> Upper,
    IReadOnlyList<RateInterval> Rates,
    double Score,
    string State);

public sealed record SegmentModel(
    string SegmentId,
    double TopM,
    double BottomM,
    string TopBoundary,
    string BottomBoundary,
    IReadOnlyList<string> AnchorIds,
    string State);

public sealed record ModelSet(
    string Digest,
    string ParameterVersion,
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments);
