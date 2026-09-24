using System.Text.Json;
using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Engine;

/// <summary>Branch head: which event a branch currently points at.</summary>
public sealed record BranchState(string BranchId, string HeadEventId, string? FrozenCandidateId, string Label);

/// <summary>The state reconstructable at any event: package version in force,
/// ruleset, latest derivation and the accept/freeze decision. Replaying to an
/// old event yields the OLD conclusions, never the newest computation.</summary>
public sealed record ReplayState(
    string AtEventId,
    string BranchId,
    CorePackage? Package,
    Ruleset Ruleset,
    EngineResult? Result,
    string? AcceptedCandidateId,
    string? RejectedCandidateId,
    string? FrozenCandidateId,
    IReadOnlyList<string> History,
    IReadOnlyList<Engine.Verdict> GeometricVerdicts);

public static class Replay
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Replay events on one branch up to (and including) a target event.
    /// Packages are supplied by the caller from immutable storage.</summary>
    public static ReplayState ToEvent(
        IEnumerable<ChronoEvent> branchEvents,
        IReadOnlyDictionary<string, CorePackage> packagesById,
        string targetEventId)
    {
        var ordered = branchEvents.OrderBy(e => e.Sequence).ToList();
        var stop = ordered.FindIndex(e => e.EventId == targetEventId);
        if (stop < 0) throw new ArgumentException($"事件 {targetEventId} 不在该分支上");
        var slice = ordered.Take(stop + 1).ToList();

        CorePackage? pkg = null;
        var ruleset = Ruleset.V1;
        EngineResult? result = null;
        string? accepted = null, rejected = null, frozen = null;
        var history = new List<string>();

        foreach (var ev in slice)
        {
            history.Add(ev.EventId);
            switch (ev.Kind)
            {
                case EventKind.PackageImported or EventKind.SourceAmended:
                {
                    var p = JsonSerializer.Deserialize<ImportPayload>(ev.PayloadJson, JsonOpts)!;
                    if (packagesById.TryGetValue(p.PackageSnapshotId, out var loaded)) pkg = loaded;
                    break;
                }
                case EventKind.RulesetChosen:
                {
                    var p = JsonSerializer.Deserialize<RulesetPayload>(ev.PayloadJson, JsonOpts)!;
                    ruleset = Rulesets.Get(p.Version);
                    break;
                }
                case EventKind.DerivationRun:
                {
                    if (pkg is not null) result = AgeModelEngine.Solve(pkg, ruleset);
                    break;
                }
                case EventKind.CandidateAccepted:
                {
                    var p = JsonSerializer.Deserialize<DecisionPayload>(ev.PayloadJson, JsonOpts)!;
                    accepted = p.CandidateId; rejected = null;
                    break;
                }
                case EventKind.CandidateRejected:
                {
                    var p = JsonSerializer.Deserialize<DecisionPayload>(ev.PayloadJson, JsonOpts)!;
                    rejected = p.CandidateId;
                    break;
                }
                case EventKind.CandidateFrozen:
                {
                    var p = JsonSerializer.Deserialize<DecisionPayload>(ev.PayloadJson, JsonOpts)!;
                    frozen = p.CandidateId; accepted ??= p.CandidateId;
                    break;
                }
                case EventKind.ParameterAdjusted:
                {
                    var p = JsonSerializer.Deserialize<RulesetPayload>(ev.PayloadJson, JsonOpts)!;
                    ruleset = Rulesets.Get(p.Version);
                    break;
                }
            }
        }

        var geoVerdicts = pkg is null ? Array.Empty<Verdict>() : Geometry.Build(pkg).Verdicts;
        return new ReplayState(targetEventId, slice[^1].BranchId, pkg, ruleset, result,
            accepted, rejected, frozen, history, geoVerdicts);
    }
}

public static class Rulesets
{
    private static readonly Dictionary<string, Ruleset> All = new()
    {
        ["rules-1.0"] = Ruleset.V1,
        ["rules-1.1"] = Ruleset.V1 with
        {
            Version = "rules-1.1",
            MonteCarloDraws = 800,
            Interpolation = InterpolationKind.Linear
        }
    };

    public static Ruleset Get(string version) =>
        All.TryGetValue(version, out var r) ? r : throw new ArgumentException($"未知规则版本 {version}");
}

public sealed record ImportPayload(string PackageSnapshotId, string CaseId, string Version, string Note);
public sealed record RulesetPayload(string Version, string Note);
public sealed record DecisionPayload(string CandidateId, string Reason, string BasisEventId);
