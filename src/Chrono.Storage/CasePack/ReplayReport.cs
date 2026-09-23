using Chrono.Core.Engine;

namespace Chrono.Storage.CasePack;

public enum ReplayStatus { Passed, RejectedAsDesigned, FailedUnexpected }

public sealed record ReplayStep(
    string StepId,
    string Title,
    string BranchId,
    IReadOnlyList<string> Scenarios,
    string Status,
    string? EventId,
    string InputExcerpt,
    string Verdict,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<string> EvidenceEventIds);

public sealed record ReplayReport(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<ReplayStep> Steps,
    bool AllPassed,
    IReadOnlyDictionary<string, object> Metrics);

public sealed record ScenarioVerification(
    string Scenario,
    bool Present,
    string Detail);
