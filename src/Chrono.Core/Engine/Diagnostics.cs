namespace Chrono.Core.Engine;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed record BuildDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string SourceId,
    string Field,
    int? Line,
    string Message,
    string? RawSnippet)
{
    public string Position => Line.HasValue ? $"{SourceId}#{Field}:L{Line}" : $"{SourceId}#{Field}";
}

public enum BuildStatus { Success, SemanticallyUnsolvable, BudgetExhausted, FormatCorrupt }

public sealed record BuildBudget(int MaxCandidates, int MaxSamples, TimeSpan TimeLimit)
{
    public static BuildBudget Default => new(64, 20000, TimeSpan.FromSeconds(10));
    public static BuildBudget Tiny => new(2, 8, TimeSpan.FromMilliseconds(1));
}

public sealed record BuildReport(
    BuildStatus Status,
    string Summary,
    IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == BuildStatus.Success;
}
