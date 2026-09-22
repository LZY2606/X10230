using ChronoCore.Domain.Events;
using ChronoCore.Domain.Quantities;

namespace ChronoCore.Domain.Derivation;

internal sealed record OrderedAnchor(Anchor Anchor, int OrderInTie, string TieKey);

internal sealed record SegmentOrdering(string Signature, List<OrderedAnchor> Ordered);

internal sealed record SegmentPlan(string SegmentId, Segment Segment, List<SegmentOrdering> Orderings, List<string> Notes);

public sealed partial class AgeModelEngine
{
    private static SegmentPlan BuildSegmentPlan(Segment seg, List<Anchor> anchors,
        ProjectState state, RuleSet rs, List<Diagnostic> diags)
    {
        var notes = new List<string>();

        // 深度重复诊断：重复深度不静默合并，记录并各自保留（同深度进入并列组）。
        var dupGroups = anchors.GroupBy(a => a.Depth.ValueM!.Value)
            .Where(g => g.Count() > 1).ToList();
        foreach (var g in dupGroups)
        {
            var ids = string.Join(",", g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
            diags.Add(Diag("D-DUPDEPTH", DiagnosticSeverity.Warning,
                $"段 {seg.Id} 深度 {g.Key:0.###} m 存在 {g.Count()} 个重复锚点（{ids}）：不自动剔除，进入并列处理",
                $"segments[{seg.Id}]", ids, null));
            notes.Add($"dupdepth@{g.Key:0.###}:{ids}");
            foreach (var a in g)
                if (a.TieGroupId is null) a.TieGroupId = $"tie-{seg.Id}-d{g.Key:0.###}";
        }

        // 并列组：显式 tieGroupId 或同深度。
        var tieGroups = anchors
            .Where(a => a.TieGroupId is not null)
            .GroupBy(a => a.TieGroupId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToList());

        // 单锚点（无并列）。
        var fixedAnchors = anchors.Where(a => a.TieGroupId is null)
            .Select(a => new OrderedAnchor(a, 0, ""))
            .ToList();

        var orderings = new List<SegmentOrdering>();
        if (tieGroups.Count == 0)
        {
            var order = fixedAnchors
                .OrderBy(a => a.Anchor.Depth.ValueM!.Value)
                .ThenBy(a => a.Anchor.Id, StringComparer.Ordinal)
                .ToList();
            orderings.Add(new SegmentOrdering("fixed", order));
        }
        else
        {
            // 每个并列组做稳定全排列；非并列锚点深度位置固定，组内排列插入其深度槽。
            var permutations = new Dictionary<string, List<List<Anchor>>>();
            foreach (var (key, members) in tieGroups)
            {
                var perms = Permutations(members);
                if (members.Count > 1)
                    diags.Add(Diag("D-TIE", DiagnosticSeverity.Info,
                        $"段 {seg.Id} 并列组 {key} 含 {members.Count} 个等优锚点，枚举 {perms.Count} 种稳定顺序，全部保留",
                        $"segments[{seg.Id}].tieGroups[{key}]", key));
                permutations[key] = perms;
            }

            foreach (var combo in CrossProduct(permutations))
            {
                var entries = new List<(Anchor Anchor, int TieOrder, string TieKey)>();
                foreach (var fa in fixedAnchors)
                    entries.Add((fa.Anchor, 0, ""));
                foreach (var (key, perm) in combo)
                    for (int i = 0; i < perm.Count; i++)
                        entries.Add((perm[i], i + 1, key));
                var ordered = entries
                    .OrderBy(e => e.Anchor.Depth.ValueM!.Value)
                    .ThenBy(e => e.TieKey, StringComparer.Ordinal)
                    .ThenBy(e => e.TieOrder)
                    .Select(e => new OrderedAnchor(e.Anchor, e.TieOrder, e.TieKey))
                    .ToList();
                var sig = string.Join("|", ordered.Select(o =>
                    o.TieKey == "" ? o.Anchor.Id : $"{o.TieKey}={o.Anchor.Id}#{o.OrderInTie}"));
                orderings.Add(new SegmentOrdering(sig, ordered));
            }
        }

        // 去重（不同 tie 组可能给出相同顺序）并稳定排序签名。
        var distinct = orderings
            .GroupBy(o => o.Signature)
            .Select(g => g.First())
            .OrderBy(o => o.Signature, StringComparer.Ordinal)
            .ToList();

        return new SegmentPlan(seg.Id, seg, distinct, notes);
    }

    internal static List<List<T>> Permutations<T>(IReadOnlyList<T> items)
        => Heap(items.ToList()).Select(x => new List<T>(x)).ToList();

    private static IEnumerable<List<T>> Heap<T>(List<T> items)
    {
        var arr = new List<T>(items);
        var c = new int[arr.Count];
        yield return new List<T>(arr);
        int i = 0;
        while (i < arr.Count)
        {
            if (c[i] < i)
            {
                if (i % 2 == 0) (arr[0], arr[i]) = (arr[i], arr[0]);
                else (arr[c[i]], arr[i]) = (arr[i], arr[c[i]]);
                yield return new List<T>(arr);
                c[i]++;
                i = 0;
            }
            else { c[i] = 0; i++; }
        }
    }

    internal static List<Dictionary<string, List<Anchor>>> CrossProduct(
        Dictionary<string, List<List<Anchor>>> groups)
    {
        var keys = groups.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var result = new List<Dictionary<string, List<Anchor>>>();
        void Recurse(int idx, Dictionary<string, List<Anchor>> acc)
        {
            if (idx == keys.Count) { result.Add(new Dictionary<string, List<Anchor>>(acc)); return; }
            foreach (var perm in groups[keys[idx]]!)
            {
                acc[keys[idx]] = perm;
                Recurse(idx + 1, acc);
                acc.Remove(keys[idx]);
            }
        }
        Recurse(0, new Dictionary<string, List<Anchor>>());
        return result;
    }

    private static List<List<SegmentOrdering>> EnumerateCombinations(List<SegmentPlan> plans, int budget)
    {
        var result = new List<List<SegmentOrdering>>();
        var orderedPlans = plans.OrderBy(p => p.SegmentId, StringComparer.Ordinal).ToList();
        bool stopped = false;
        void Recurse(int idx, List<SegmentOrdering> acc)
        {
            if (stopped) return;
            if (idx == orderedPlans.Count)
            {
                if (result.Count >= budget) { stopped = true; return; }
                result.Add(new List<SegmentOrdering>(acc));
                return;
            }
            foreach (var o in orderedPlans[idx].Orderings)
            {
                acc.Add(o);
                Recurse(idx + 1, acc);
                acc.RemoveAt(acc.Count - 1);
                if (stopped) return;
            }
        }
        Recurse(0, new List<SegmentOrdering>());
        return result;
    }
}
