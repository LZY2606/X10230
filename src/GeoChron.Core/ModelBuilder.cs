using System.Text.Json.Nodes;

namespace GeoChron.Core;

public sealed record BuildOutput(
    List<ModelCandidate> Models,
    FailureKind Failure,
    List<Diagnostic> Diagnostics,
    List<JsonObject> Judgments);

/// <summary>
/// Derives depth-age model candidates per continuous segment.
/// Rules enforced here (and emitted as machine-readable judgments):
///  - age is monotone only within an unbroken segment; gaps and unconformities
///    are explicit boundaries, never interpolated across;
///  - an anchor's distribution is sampled, never replaced by its mean;
///  - all equally defensible candidates are kept.
/// </summary>
public static class ModelBuilder
{
    public static BuildOutput Build(WorkspaceState state)
    {
        var diags = new List<Diagnostic>();
        var judgments = new List<JsonObject>();
        var models = new List<ModelCandidate>();
        var p = state.Params;
        var segmentsWithAnchors = 0;
        var segmentsSolved = 0;

        foreach (var seg in state.Segments.Values.OrderBy(s => s.TopM))
        {
            var anchors = state.Anchors.Values
                .Where(a => a.SegmentId == seg.Id && state.IsActive(a.Id) && !double.IsNaN(a.DepthM))
                .OrderBy(a => a.DepthM).ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            if (anchors.Count == 0)
            {
                diags.Add(Diagnostic.Info("SEG-EMPTY", $"分段 {seg.Id} 没有可用锚点，跳过建模", $"segments/{seg.Id}"));
                continue;
            }
            segmentsWithAnchors++;

            if (seg.BoundaryBelow == "gap")
                judgments.Add(Judgment("BOUNDARY-GAP", seg.Id, "分段缺口是显式边界，不跨缺口插值"));
            if (seg.BoundaryBelow == "unconformity")
                judgments.Add(Judgment("BOUNDARY-UNCONFORMITY", seg.Id, "不整合是显式边界，不跨不整合插值"));
            judgments.Add(Judgment("MONOTONY-WITHIN-SEGMENT", seg.Id,
                seg.Inverted ? "倒转层段：年龄随深度单调递减" : "年龄只能在不断层段中单调（随深度递增）"));

            var groups = TieGroups(anchors, p.TieToleranceM);
            foreach (var g in groups.Where(g => g.Count > 1))
                judgments.Add(new JsonObject
                {
                    ["code"] = "TIED-ANCHORS",
                    ["segmentId"] = seg.Id,
                    ["anchorIds"] = new JsonArray(g.Select(a => (JsonNode?)JsonValue.Create(a.Id)).ToArray()),
                    ["detail"] = "并列锚点：同一深度多个锚点，每个选择都是独立候选"
                });

            // Options per tie group: each anchor alone, plus "all" when distributions overlap.
            var groupOptions = new List<List<string[]>>();
            foreach (var g in groups)
            {
                var opts = g.Select(a => new[] { a.Id }).ToList();
                if (g.Count > 1 && Overlaps(g, p.OverlapToleranceSd))
                    opts.Add(g.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray());
                groupOptions.Add(opts);
            }

            // Inversion conflicts between consecutive groups.
            var direction = seg.Inverted ? -1.0 : 1.0;
            var conflicts = new List<(int Prev, int Next)>();
            for (var i = 1; i < groups.Count; i++)
            {
                var delta = (MedianOf(groups[i]) - MedianOf(groups[i - 1])) * direction;
                if (delta < 0) conflicts.Add((i - 1, i));
            }
            // Resolution options per conflict: drop one side's whole group.
            var conflictOptions = conflicts.Select(c => new[]
            {
                (Drop: groups[c.Prev].Select(a => a.Id).ToArray(), Kept: groups[c.Next].Select(a => a.Id).ToArray()),
                (Drop: groups[c.Next].Select(a => a.Id).ToArray(), Kept: groups[c.Prev].Select(a => a.Id).ToArray())
            }).ToList();

            long combinations = 1;
            foreach (var o in groupOptions) combinations *= o.Count;
            foreach (var o in conflictOptions) combinations *= o.Length;
            if (combinations > p.MaxCandidates)
            {
                diags.Add(Diagnostic.Error("BUDGET-CANDIDATES",
                    $"分段 {seg.Id} 需要 {combinations} 个候选组合，超过预算 {p.MaxCandidates}",
                    $"segments/{seg.Id}"));
                return new BuildOutput(models, FailureKind.BudgetExhausted, diags, judgments);
            }

            var specs = EnumerateSpecs(groups, groupOptions, conflictOptions, conflicts);
            var segDigest = SegmentDigest(state, seg);
            var validHere = 0;
            var candidateIndex = 0;
            foreach (var spec in specs)
            {
                var chosen = spec.Chosen
                    .Select(id => anchors.First(a => a.Id == id))
                    .OrderBy(a => a.DepthM).ThenBy(a => a.Id, StringComparer.Ordinal)
                    .ToList();
                if (chosen.Count < 2)
                {
                    diags.Add(Diagnostic.Info("CANDIDATE-THIN",
                        $"分段 {seg.Id} 的一个候选只剩 {chosen.Count} 个锚点，无法构成速率区间", $"segments/{seg.Id}"));
                    continue;
                }
                var candidate = Simulate(state, seg, chosen, spec.Dropped, segDigest, candidateIndex);
                candidateIndex++;
                if (candidate == null)
                {
                    diags.Add(Diagnostic.Error("CANDIDATE-REJECTED",
                        $"分段 {seg.Id} 候选 [{string.Join(",", chosen.Select(a => a.Id))}] 的单调可行样本不足",
                        $"segments/{seg.Id}"));
                    continue;
                }
                validHere++;
                models.Add(candidate);
                foreach (var a in chosen.Where(a => a.Distribution.Kind == "samples"))
                    judgments.Add(new JsonObject
                    {
                        ["code"] = "DIST-KEPT-FULL",
                        ["anchorId"] = a.Id,
                        ["modelId"] = candidate.Id,
                        ["detail"] = "锚点年龄分布按原始样本参与计算，未被均值替代"
                    });
                foreach (var d in spec.Dropped)
                    judgments.Add(new JsonObject
                    {
                        ["code"] = "INVERSION-RESOLVED",
                        ["segmentId"] = seg.Id,
                        ["droppedAnchorId"] = d,
                        ["modelId"] = candidate.Id,
                        ["detail"] = "倒置冲突通过排除一侧锚点消解，两侧方案均保留为候选"
                    });
            }

            if (validHere == 0)
            {
                diags.Add(Diagnostic.Error("SEG-UNSOLVABLE",
                    $"分段 {seg.Id} 不存在满足单调性的候选模型", $"segments/{seg.Id}"));
            }
            else
            {
                segmentsSolved++;
                if (validHere > 1)
                    judgments.Add(new JsonObject
                    {
                        ["code"] = "CANDIDATES-KEPT",
                        ["segmentId"] = seg.Id,
                        ["count"] = validHere,
                        ["detail"] = "多个等优模型全部保留"
                    });
            }
        }

        if (segmentsWithAnchors == 0)
        {
            diags.Add(Diagnostic.Error("INPUT-INCOMPLETE", "没有任何带锚点的分段，无法推导深度年龄模型", "anchors"));
            return new BuildOutput(models, FailureKind.SemanticallyUnsolvable, diags, judgments);
        }
        if (segmentsSolved == 0)
        {
            diags.Add(Diagnostic.Error("MODEL-UNSOLVABLE", "所有分段均无法满足单调性约束", "segments"));
            return new BuildOutput(models, FailureKind.SemanticallyUnsolvable, diags, judgments);
        }

        var ordered = models
            .OrderBy(m => m.SegmentId, StringComparer.Ordinal)
            .ThenBy(m => m.RejectedIterations)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        return new BuildOutput(ordered, FailureKind.None, diags, judgments);
    }

    private static ModelCandidate? Simulate(
        WorkspaceState state, CoreSegment seg, List<AgeAnchor> chosen, string[] dropped,
        string segDigest, int candidateIndex)
    {
        var p = state.Params;
        var direction = seg.Inverted ? -1.0 : 1.0;
        var specKey = string.Join(",", chosen.Select(a => a.Id));
        var seed = HashCode.Combine(p.Seed, seg.Id, specKey, candidateIndex);
        var rng = new Random(seed);
        var accepted = new List<double[]>();
        var attempts = 0;
        var maxAttempts = p.Iterations * 20;
        while (accepted.Count < p.Iterations && attempts < maxAttempts)
        {
            attempts++;
            var ages = chosen.Select(a => a.EffectiveSample(rng)).ToArray();
            var ok = true;
            for (var i = 1; i < ages.Length; i++)
                if ((ages[i] - ages[i - 1]) * direction < 0) { ok = false; break; }
            if (ok) accepted.Add(ages);
        }
        if (accepted.Count < Math.Max(10, p.Iterations / 10)) return null;

        var envelope = new DepthNode[chosen.Count];
        for (var k = 0; k < chosen.Count; k++)
        {
            var col = accepted.Select(it => it[k]).ToArray();
            envelope[k] = new DepthNode(
                chosen[k].DepthM,
                AgeDistribution.Quantile(col, 0.05),
                AgeDistribution.Quantile(col, 0.5),
                AgeDistribution.Quantile(col, 0.95));
        }

        var rates = new List<RateInterval>();
        for (var k = 1; k < chosen.Count; k++)
        {
            var dz = chosen[k].DepthM - chosen[k - 1].DepthM;
            if (dz <= 0) continue;
            var col = accepted.Select(it => dz / Math.Max(1e-9, (it[k] - it[k - 1]) / 1000.0)).ToArray();
            rates.Add(new RateInterval(
                seg.Id, chosen[k - 1].DepthM, chosen[k].DepthM,
                AgeDistribution.Quantile(col, 0.05),
                AgeDistribution.Quantile(col, 0.5),
                AgeDistribution.Quantile(col, 0.95),
                "m/kyr"));
        }

        var dependsOn = chosen.Select(a => a.Id)
            .Concat(new[] { seg.Id })
            .Concat(state.EventLayers.Values.Where(e => e.SegmentId == seg.Id).Select(e => e.Id))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var id = Hashing.ShortId("mdl", seg.Id + "|" + segDigest + "|" + specKey);
        var rationale = $"分段 {seg.Id}：锚点 [{specKey}]" +
                        (dropped.Length > 0 ? $"，排除 [{string.Join(",", dropped)}]" : "") +
                        $"，接受 {accepted.Count}/{attempts} 次单调可行抽样";
        return new ModelCandidate(
            id, seg.Id, chosen.Select(a => a.Id).ToArray(), dropped,
            segDigest, p.Version, dependsOn, envelope, rates.ToArray(),
            accepted.Count, attempts - accepted.Count, ModelStatus.Candidate, rationale);
    }

    public static string SegmentDigest(WorkspaceState state, CoreSegment seg)
    {
        var obj = new JsonObject
        {
            ["segment"] = JsonMap.ToJson(seg),
            ["anchors"] = new JsonArray(state.Anchors.Values
                .Where(a => a.SegmentId == seg.Id && state.IsActive(a.Id))
                .OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => (JsonNode?)JsonMap.ToJson(a)).ToArray()),
            ["eventLayers"] = new JsonArray(state.EventLayers.Values
                .Where(e => e.SegmentId == seg.Id)
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => (JsonNode?)JsonMap.ToJson(e)).ToArray()),
            ["exclusions"] = new JsonArray(state.Exclusions.Values
                .Where(x => x.Active && state.Anchors.TryGetValue(x.AnchorId, out var a) && a.SegmentId == seg.Id)
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => (JsonNode?)JsonMap.ToJson(x)).ToArray()),
            ["params"] = JsonMap.ToJson(state.Params)
        };
        return Hashing.Sha256(Hashing.Canonical(obj));
    }

    private static List<List<AgeAnchor>> TieGroups(List<AgeAnchor> sorted, double tol)
    {
        var groups = new List<List<AgeAnchor>>();
        foreach (var a in sorted)
        {
            if (groups.Count > 0 && Math.Abs(groups[^1][0].DepthM - a.DepthM) <= tol)
                groups[^1].Add(a);
            else
                groups.Add(new List<AgeAnchor> { a });
        }
        return groups;
    }

    private static double MedianOf(List<AgeAnchor> g) => g.Average(a => a.EffectiveMedian);

    private static bool Overlaps(List<AgeAnchor> g, double tolSd)
    {
        for (var i = 0; i < g.Count; i++)
        for (var j = i + 1; j < g.Count; j++)
        {
            var spread = tolSd * (g[i].Distribution.Sd + g[j].Distribution.Sd);
            if (Math.Abs(g[i].EffectiveMedian - g[j].EffectiveMedian) > spread) return false;
        }
        return true;
    }

    private sealed record Spec(string[] Chosen, string[] Dropped);

    private static List<Spec> EnumerateSpecs(
        List<List<AgeAnchor>> groups,
        List<List<string[]>> groupOptions,
        List<(string[] Drop, string[] Kept)[]> conflictOptions,
        List<(int Prev, int Next)> conflicts)
    {
        var specs = new List<Spec> { new(Array.Empty<string>(), Array.Empty<string>()) };
        // tie-group choices
        foreach (var opts in groupOptions)
        {
            var next = new List<Spec>();
            foreach (var s in specs)
            foreach (var o in opts)
                next.Add(new Spec(s.Chosen.Concat(o).ToArray(), s.Dropped));
            specs = next;
        }
        // conflict resolutions: each conflict drops one side's group; dedupe identical outcomes
        foreach (var opts in conflictOptions)
        {
            var next = new List<Spec>();
            foreach (var s in specs)
            foreach (var o in opts)
            {
                var dropped = s.Dropped.Concat(o.Drop).Distinct(StringComparer.Ordinal).ToArray();
                var chosen = s.Chosen.Where(id => !dropped.Contains(id)).ToArray();
                next.Add(new Spec(chosen, dropped));
            }
            specs = next;
        }
        return specs
            .GroupBy(s => string.Join("|", s.Chosen.OrderBy(x => x, StringComparer.Ordinal)))
            .Select(g => g.First())
            .ToList();
    }

    private static JsonObject Judgment(string code, string segmentId, string detail) => new JsonObject
    {
        ["code"] = code, ["segmentId"] = segmentId, ["detail"] = detail
    };
}
