using System.Text.Json;

namespace AgeDepth;

public record ArchiveEntry(string ScenarioId, string Title, string Rule, string Outcome,
    string[] DiagnosticCodes, string[] AffectedSegments, string[] KeptCandidateIds, string Detail);

/// <summary>
/// Builds the counterexample archive: a baseline snapshot, then one-source-at-a-time
/// perturbations. Adopted and rejected results are both kept, and each judgment
/// (R1 monotonic-only-in-continuous-segment, R2 distribution-never-mean,
/// R3 keep-all-equal-candidates) is recorded as a machine-readable event.
/// </summary>
public static class ArchiveBuilder
{
    public static List<ArchiveEntry> Build(Store store)
    {
        var state = CasePack.Replay(store, null);
        var entries = new List<ArchiveEntry>();

        // Baseline: everything preserved.
        var baseline = ModelEngine.Compute(state.Segments, state.Anchors, state.Params);
        entries.Add(new ArchiveEntry("baseline", "基线：全部材料与排除决定保留", "R0",
            baseline.Kind.ToString(),
            baseline.Segments.SelectMany(s => s.Diagnostics).Select(d => d.Code).ToArray(),
            baseline.Segments.Select(s => s.SegmentId).ToArray(),
            baseline.AllCandidates.Select(c => c.Id).ToArray(),
            "深度基准/分段缺口/放射性年龄分布/储层偏移/事件层相关/排除决定均保留"));

        // depth-duplicate: one new anchor at an existing depth.
        Run("depth-duplicate", "深度重复", "R3", state,
            anchors => anchors.Add(MkAnchor("anc-02b", "seg-A", 2.10, 4.90, 5.05, 5.20)),
            r => r.Segments.SelectMany(s => s.Diagnostics).Any(d => d.Code == "DEPTH_DUPLICATE"),
            "并列锚点各自成候选", store, entries);

        // unconformity-boundary: no candidate may cross unc-1.
        Run("unconformity-boundary", "不整合是显式边界", "R1", state, _ => { },
            r => r.AllCandidates.Where(c => c.Status == "accepted").All(c =>
                c.DepthKnots.All(d => d < 7.80) || c.DepthKnots.All(d => d >= 7.80)),
            "候选不跨越 unc-1；seg-B 与 seg-C 各自单调", store, entries);

        // inverted-section: make anc-03 younger than anc-01.
        Run("inverted-section", "倒置层段", "R1", state,
            anchors => anchors.ReplaceAll("anc-03", a => a with
            {
                Age = new AgeDistribution("samples", new[] { 0.8, 0.9, 1.0 }, "ka BP")
            }),
            r => r.Segments.SelectMany(s => s.Diagnostics).Any(d => d.Code == "INVERTED_SECTION")
                 && r.AllCandidates.Any(c => c.Status == "rejected")
                 && r.AllCandidates.Any(c => c.Status == "accepted" && c.ExcludedAnchorIds.Contains("anc-03")),
            "原序列保留为 rejected；排除单锚后得到 accepted", store, entries);

        // tied-anchors: same depth, different ages, both kept.
        Run("tied-anchors", "并列锚点", "R3", state,
            anchors => anchors.Add(MkAnchor("anc-06b", "seg-C", 8.10, 25.2, 25.5, 25.8)),
            r => r.Segments.Where(s => s.SegmentId == "seg-C")
                     .SelectMany(s => s.Candidates).Count(c => c.Status == "accepted") >= 2,
            "同一深度两个锚点保留为等优候选", store, entries);

        // long-tail: band must be asymmetric, distribution never replaced by mean.
        Run("long-tail", "年龄分布长尾", "R2", state, _ => { },
            r =>
            {
                var cand = r.Segments.Where(s => s.SegmentId == "seg-A")
                    .SelectMany(s => s.Candidates).FirstOrDefault(c => c.UsedAnchorIds.Contains("anc-03"));
                if (cand is null) return false;
                var i = Array.IndexOf(cand.UsedAnchorIds, "anc-03");
                var med = cand.AgeMedian[i];
                return cand.AgeP95[i] - med > med - cand.AgeP05[i];
            },
            "anc-03 的 p95 远离中位数，不确定度带不对称", store, entries);

        // local-recompute: scripted source change only recomputes seg-B.
        {
            var before = ModelEngine.Compute(state.Segments, state.Anchors, state.Params);
            var hashes = before.Segments.ToDictionary(s => s.SegmentId, s => s.InputHash);
            var changed = state.Anchors.Select(a => a.Id == "anc-05-v2"
                    ? a with { Depth = Quantity.Of(6.50, "mbsf", "mbsf") } : a).ToList();
            var after = ModelEngine.Compute(state.Segments, changed, state.Params, hashes);
            var recomputed = after.Segments.Where(s => s.Recomputed).Select(s => s.SegmentId).ToArray();
            var ok = recomputed.SequenceEqual(new[] { "seg-B" });
            entries.Add(new ArchiveEntry("local-recompute", "局部重算", "R4",
                ok ? "confirmed" : "violated", Array.Empty<string>(), recomputed,
                after.AllCandidates.Select(c => c.Id).ToArray(),
                "改动 anc-05 后仅 seg-B 重算"));
            AppendJudgment(store, entries[^1], ok);
        }

        // concurrent-freeze: two freezes at the same head; one wins, one conflicts.
        {
            var head = store.HeadSeq();
            var first = store.Freeze(head, "{\"model\":\"archive-freeze-A\"}");
            var second = store.Freeze(head, "{\"model\":\"archive-freeze-B\"}");
            var ok = first.Kind == ResultKind.Ok && second.Kind == ResultKind.Conflict;
            entries.Add(new ArchiveEntry("concurrent-freeze", "并发冻结", "R5",
                ok ? "confirmed" : "violated", Array.Empty<string>(), Array.Empty<string>(),
                new[] { first.Event?.Id ?? "", second.Event?.Id ?? "" },
                "第一次 frozen，第二次 conflict，两次尝试都留痕"));
            AppendJudgment(store, entries[^1], ok);
        }

        return entries;
    }

    private static AgeAnchor MkAnchor(string id, string seg, double depth, params double[] ages) =>
        new(id, seg, Quantity.Of(depth, "mbsf", "mbsf"),
            new AgeDistribution("samples", ages, "ka BP"), null, "archive-perturbation");

    private static void Run(string id, string title, string rule, CaseState state,
        Action<List<AgeAnchor>> mutate, Func<ComputationResult, bool> verify, string detail,
        Store store, List<ArchiveEntry> entries)
    {
        var anchors = state.Anchors.ToList();
        mutate(anchors);
        var r = ModelEngine.Compute(state.Segments, anchors, state.Params);
        var ok = verify(r);
        var entry = new ArchiveEntry(id, title, rule, ok ? "confirmed" : "violated",
            r.Segments.SelectMany(s => s.Diagnostics).Select(d => d.Code).Distinct().ToArray(),
            r.Segments.Where(s => s.Diagnostics.Count > 0 || s.Candidates.Count > 0).Select(s => s.SegmentId).ToArray(),
            r.AllCandidates.Select(c => c.Id + ":" + c.Status).ToArray(), detail);
        entries.Add(entry);
        AppendJudgment(store, entry, ok);
    }

    private static void AppendJudgment(Store store, ArchiveEntry entry, bool ok)
    {
        store.AppendEvent("judgment", Array.Empty<string>(), "params/v2",
            Hashing.Sha256(entry.ScenarioId + entry.Outcome),
            JsonSerializer.Serialize(new
            {
                scenario = entry.ScenarioId, rule = entry.Rule, outcome = ok ? "confirmed" : "violated",
                diagnostics = entry.DiagnosticCodes, affected = entry.AffectedSegments,
                kept = entry.KeptCandidateIds, detail = entry.Detail
            }, Hashing.Json), ok ? "confirmed" : "violated");
    }
}

internal static class AnchorListExt
{
    public static void ReplaceAll(this List<AgeAnchor> list, string id, Func<AgeAnchor, AgeAnchor> f)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == id) list[i] = f(list[i]);
    }
}
