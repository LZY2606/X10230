using System.Text;
using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Engine;

/// <summary>A single defensible depth-age model. A candidate is defined by
/// exactly which anchors it admits; ties and disputed exclusions are handled
/// by enumerating choices rather than picking one silently.</summary>
public sealed record CandidateModel(
    string Id,
    int Rank,
    bool Optimal,
    double Score,
    IReadOnlyList<string> AdmittedAnchorIds,
    IReadOnlyList<string> ExcludedAnchorIds,
    IReadOnlyList<string> TieGroupIds,
    IReadOnlyDictionary<string, SectionCurve> Curves,
    IReadOnlyList<Verdict> Verdicts,
    string CanonicalKey);

/// <summary>Monte-Carlo depth-age result for one continuous section.</summary>
public sealed record SectionCurve(
    string SectionId,
    double[] DepthsCm,
    double[] Q025,
    double[] Q500,
    double[] Q975,
    double[] RateQ025CmPerYear,
    double[] RateQ500CmPerYear,
    double[] RateQ975CmPerYear);

/// <summary>An equivalence class of anchors sharing a depth within tolerance.
/// The system enumerates choices only when a choice is actually required
/// (e.g. inconsistent ages at the same depth).</summary>
public sealed record TieGroup(string Key, IReadOnlyList<NormAnchor> Anchors)
{
    public bool AgesConflict =>
        Anchors.Count > 1 &&
        Anchors.Max(a => a.Raw.Age.Q(0.025)) > Anchors.Min(a => a.Raw.Age.Q(0.975)) + 1e-6;
}

/// <summary>Enumerates anchor-choice combinations. Each tie group with
/// conflicting distributions yields one option per anchor; consistent ties
/// are admitted together. Manual exclusions are honored, but excluded
/// materials remain visible in provenance.</summary>
public static class CandidateEnumerator
{
    public sealed record ChoiceSet(IReadOnlyList<IReadOnlyList<NormAnchor>> Options, string Signature);

    public static List<ChoiceSet> Build(GeometryModel geo, CorePackage pkg)
    {
        var excluded = pkg.Exclusions.Select(e => e.TargetAnchorOrMaterialId).ToHashSet();
        var usable = geo.Anchors
            .Where(a => a.DepthOk && !excluded.Contains(a.Id) && !excluded.Contains(a.Raw.SourceMaterialId))
            .ToList();

        var groups = new Dictionary<string, List<NormAnchor>>();
        foreach (var a in usable.OrderBy(x => x.Depth).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            var key = groups.Keys.FirstOrDefault(k =>
                groups[k].Any(g => Math.Abs(g.Depth - a.Depth) <= Geometry.TieEpsilonCm));
            if (key is null) { key = "TG-" + groups.Count.ToString("D2"); groups[key] = new(); }
            groups[key].Add(a);
        }

        var sets = new List<ChoiceSet>();
        foreach (var kv in groups.OrderBy(kv => kv.Value[0].Depth))
        {
            var g = new TieGroup(kv.Key, kv.Value);
            if (g.Anchors.Count <= 1)
            {
                sets.Add(new ChoiceSet(new[] { g.Anchors }, kv.Key + ":S"));
                continue;
            }
            if (!g.AgesConflict)
            {
                // consistent parallel anchors: keep both together (averaging is forbidden)
                sets.Add(new ChoiceSet(new[] { g.Anchors }, kv.Key + ":JOIN"));
                continue;
            }
            // conflicting tie: every anchor is a distinct legal option
            var options = g.Anchors.Select(a => (IReadOnlyList<NormAnchor>)new[] { a }).ToList();
            sets.Add(new ChoiceSet(options, kv.Key + ":X" + g.Anchors.Count));
        }
        return sets;
    }

    public static IEnumerable<List<NormAnchor>> Expand(IReadOnlyList<ChoiceSet> sets)
    {
        List<List<NormAnchor>> combos = new() { new() };
        foreach (var set in sets)
        {
            var next = new List<List<NormAnchor>>();
            foreach (var prefix in combos)
            foreach (var option in set.Options)
            {
                var grown = prefix.Concat(option).ToList();
                next.Add(grown);
            }
            combos = next;
        }
        return combos
            .Select(c => c.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal).ToList());
    }

    public static string CanonicalKey(IReadOnlyList<NormAnchor> admitted, string rulesetFingerprint)
    {
        var sb = new StringBuilder(rulesetFingerprint).Append('|');
        sb.AppendJoin(",", admitted.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal));
        return sb.ToString();
    }
}
