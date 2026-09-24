namespace AgeDepth.Core;

public enum Severity { Info, Warning, Error }

/// <summary>A structured, positioned diagnostic. Never a bare log line.</summary>
public sealed record Diagnostic(string Code, string Message, string Path, Severity Severity);

/// <summary>
/// The three failure kinds are deliberately distinct results, not log lines:
/// resource budget exhausted, input format corrupt, semantics unsolvable.
/// </summary>
public enum ComputeStatus { Ok, BudgetExhausted, FormatCorrupt, Unsolvable }

/// <summary>A machine-readable judgment emitted by a business rule.</summary>
public sealed record Judgment(string Rule, string Subject, string Outcome, string Rationale);

public static class RuleIds
{
    public const string MonotonicWithinSegment = "age-monotonic-within-unbroken-segment";
    public const string GapIsExplicitBoundary = "gap-and-unconformity-are-explicit-boundaries";
    public const string DistributionNotMean = "anchor-distribution-not-replaced-by-mean";
    public const string EqualCandidatesRetained = "equal-quality-candidates-retained";
}
