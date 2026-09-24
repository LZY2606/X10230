using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Engine;

/// <summary>Result of changing exactly one direct source. The impacted set
/// must stay scoped: only anchors/candidates/sections that depend on the
/// changed material may move. This is the machine-readable evidence the
/// counterexample archive stores for accepted and rejected outcomes alike.</summary>
public sealed record ImpactReport(
    string BaselineDigest,
    string ChangedDigest,
    string ChangedMaterialId,
    string ChangeKind,
    IReadOnlyList<string> DirectlyChangedIds,
    IReadOnlyList<string> RecomputedSectionIds,
    IReadOnlyList<string> AffectedCandidateIds,
    IReadOnlyList<string> UnaffectedCandidateIds,
    IReadOnlyList<string> StableAnchorIds,
    bool LocalOnly,
    IReadOnlyList<string> AdoptedOutcomeIds,
    IReadOnlyList<string> RejectedOutcomeIds,
    IReadOnlyList<ImpactEvent> Events);

public sealed record ImpactEvent(string Code, string Severity, string Message, IReadOnlyList<string> AffectedIds);

/// <summary>Describes which sections an anchor feeds; this dependency edge is
/// what makes recompute "local" rather than a full recomputation.</summary>
public sealed record DependencyGraph(
    IReadOnlyDictionary<string, string[]> AnchorToSections,
    IReadOnlyDictionary<string, string[]> SectionToAnchors,
    IReadOnlyDictionary<string, string[]> CandidateToSections);

public static class Dependency
{
    public static DependencyGraph Build(CorePackage pkg, EngineResult baseline)
    {
        var geo = Geometry.Build(pkg);
        var a2s = new Dictionary<string, HashSet<string>>();
        var s2a = new Dictionary<string, HashSet<string>>();
        foreach (var section in geo.Sections)
        {
            s2a[section.SectionId] = new HashSet<string>();
            foreach (var a in geo.Anchors.Where(a => a.DepthOk &&
                a.Depth >= section.Top.Value - Geometry.TieEpsilonCm &&
                a.Depth <= section.Bottom.Value + Geometry.TieEpsilonCm))
            {
                if (!a2s.TryGetValue(a.Id, out var set)) a2s[a.Id] = set = new();
                set.Add(section.SectionId);
                s2a[section.SectionId].Add(a.Id);
            }
        }
        var c2s = new Dictionary<string, string[]>();
        foreach (var c in baseline.Candidates)
            c2s[c.Id] = c.Curves.Keys.OrderBy(x => x).ToArray();
        return new DependencyGraph(
            a2s.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToArray()),
            s2a.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToArray()),
            c2s);
    }
}

public static class ImpactAnalyzer
{
    public static ImpactReport Compare(
        CorePackage baseline, EngineResult baselineResult,
        CorePackage changed, string changedMaterialId, string changeKind,
        EngineResult changedResult, Ruleset rules)
    {
        var graph = Dependency.Build(baseline, baselineResult);

        // Directly changed materials: anchor whose material key changed, or the
        // single amended source object.
        var baseByMat = baseline.Anchors.ToDictionary(a => a.Id);
        var changedIds = new HashSet<string>();
        foreach (var a in changed.Anchors)
            if (!baseByMat.TryGetValue(a.Id, out var b) || b.MaterialKey != a.MaterialKey)
                changedIds.Add(a.Id);
        if (changedIds.Count == 0) changedIds.Add(changedMaterialId);

        var recomputedSections = changedIds
            .Where(graph.AnchorToSections.ContainsKey)
            .SelectMany(id => graph.AnchorToSections[id])
            .ToHashSet();

        var affected = baselineResult.Candidates
            .Where(c => c.AdmittedAnchorIds.Any(changedIds.Contains) ||
                        c.Curves.Keys.Any(recomputedSections.Contains))
            .Select(c => c.Id).ToHashSet();
        var unaffected = baselineResult.Candidates
            .Select(c => c.Id).Where(id => !affected.Contains(id)).ToList();

        var stableAnchors = baseline.Anchors
            .Where(a => !changedIds.Contains(a.Id))
            .Select(a => a.Id).OrderBy(x => x).ToList();

        // Match candidates across runs by canonical anchor set
        var changedByKey = changedResult.Candidates.ToDictionary(c => c.CanonicalKey);
        var adopted = new List<string>();
        var rejected = new List<string>();
        var events = new List<ImpactEvent>();

        foreach (var c in baselineResult.Candidates)
        {
            var now = changedResult.Candidates.FirstOrDefault(x => AnchorSetEqual(x, c));
            if (now is null)
            {
                rejected.Add(c.Id);
                events.Add(new("CANDIDATE_ELIMINATED", "warning",
                    $"候选 {c.Id} 在改动后不再可行", c.AdmittedAnchorIds.ToArray()));
            }
            else if (Math.Abs(now.Score - c.Score) > 1e-9)
            {
                affected.Add(now.Id);
                events.Add(new("CANDIDATE_RESCORED", "warning",
                    $"候选 {c.Id} 分数 {c.Score:0.###} → {now.Score:0.###}", new[] { c.Id, now.Id }));
            }
        }
        foreach (var c in changedResult.Candidates)
            if (!baselineResult.Candidates.Any(x => AnchorSetEqual(x, c)))
            {
                adopted.Add(c.Id);
                events.Add(new("CANDIDATE_EMERGED", "warning",
                    $"改动后出现新候选 {c.Id}", c.AdmittedAnchorIds.ToArray()));
            }

        var localOnly = recomputedSections.Count < graph.SectionToAnchors.Count;
        return new ImpactReport(
            AgeModelEngine.Digest(baseline), AgeModelEngine.Digest(changed),
            changedMaterialId, changeKind,
            changedIds.OrderBy(x => x).ToArray(),
            recomputedSections.OrderBy(x => x).ToArray(),
            affected.OrderBy(x => x).ToArray(),
            unaffected,
            stableAnchors,
            localOnly,
            adopted, rejected, events);
    }

    private static bool AnchorSetEqual(CandidateModel a, CandidateModel b)
    {
        var x = new HashSet<string>(a.AdmittedAnchorIds);
        return x.SetEquals(b.AdmittedAnchorIds);
    }
}

/// <summary>Concurrency/freeze guard. A frozen candidate pins a ruleset and
/// input digest; deriving or accepting on a changed base while frozen is a
/// distinct conflict, not a silent overwrite.</summary>
public static class FreezeGuard
{
    public sealed record FreezeDecision(string CandidateId, string InputDigest, string RulesetFingerprint);

    public enum FreezeOutcome { Allowed, FrozenConflict }

    public static (FreezeOutcome Outcome, string? ConflictCode, string? Message) TryCompareNewAnchor(
        FreezeDecision frozen, EngineResult current, string newAnchorId)
    {
        if (current.InputDigest != frozen.InputDigest)
            return (FreezeOutcome.FrozenConflict, "FROZEN_BASE_CHANGED",
                $"模型已冻结于输入摘要 {frozen.InputDigest}；当前摘要 {current.InputDigest} 已变化，" +
                $"新锚点 {newAnchorId} 的影响只能在比较视图中读取，不得回写覆盖冻结结论");
        if (!current.RulesetFingerprint.Equals(frozen.RulesetFingerprint, StringComparison.Ordinal))
            return (FreezeOutcome.FrozenConflict, "FROZEN_RULES_CHANGED",
                "规则版本已变化，冻结模型不允许用新规则重算覆盖");
        return (FreezeOutcome.Allowed, null, null);
    }
}
