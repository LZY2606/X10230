using System.Text;

namespace ChronoAge.Core;

/// <summary>
/// Enumerates the legal candidate models from structural ambiguity. Each ambiguity multiplies
/// the candidate set; if the product exceeds the parameter cap the outcome is
/// <see cref="OutcomeKind.BudgetExhausted"/> — distinct from "no legal model"
/// (<see cref="OutcomeKind.SemanticallyUnsolvable"/>) and from "missing input"
/// (<see cref="OutcomeKind.Incomplete"/>).
/// </summary>
public static class CandidateTree
{
    public static Outcome<IReadOnlyList<ModelCandidate>> Enumerate(
        ProjectState st,
        IReadOnlyList<DepoSegment> segments,
        IReadOnlyList<DerivedAnchor> anchors)
    {
        var diags = new List<Diagnostic>();

        // 1) Structural checks against the ordered section layout.
        var sections = st.OrderedSections().ToList();
        for (var i = 0; i + 1 < sections.Count; i++)
        {
            if (sections[i].Bottom.Canonical > sections[i + 1].Top.Canonical + st.Parameters.DepthEpsilonM)
            {
                var overlapKey = $"OVL:{sections[i].LogId}|{sections[i + 1].LogId}";
                var call = st.OverlapCalls.Values.FirstOrDefault(c => c.BoundaryLogId == sections[i].LogId
                    || c.BoundaryLogId == sections[i + 1].LogId || c.BoundaryLogId == overlapKey);
                if (call is null)
                {
                    diags.Add(Diag.Error("ENG_INVERTED_SECTION",
                        $"Sections {sections[i].Code} and {sections[i + 1].Code} overlap in depth without an explicit boundary decision.",
                        sections[i + 1].Id, "top"));
                    return Outcome<IReadOnlyList<ModelCandidate>>.Incomplete(diags);
                }
            }
        }

        // 2) Anchor placement: anchors inside a known depo segment; orphans are diagnostics.
        foreach (var a in anchors)
        {
            var inside = segments.Any(s => a.DepthM >= s.TopDepthM - st.Parameters.DepthEpsilonM
                                        && a.DepthM <= s.BottomDepthM + st.Parameters.DepthEpsilonM);
            if (!inside)
                diags.Add(Diag.Warn("ENG_ANCHOR_IN_GAP",
                    $"Anchor {a.AnchorId} at {a.DepthM.ToString("0.###")} m falls outside any continuously deposited segment (gap/unconformity); kept but not interpolated.",
                    a.SourceLogId, "depth"));
        }

        // 3) Branch factors: tie orders for duplicate-depth anchors (2 each);
        //    a human call fixes the branch to one ordering.
        var tieGroups = new List<(DerivedAnchor a, DerivedAnchor b)>();
        for (var i = 0; i + 1 < anchors.Count; i++)
            if (anchors[i].DuplicateDepthWithNext)
                tieGroups.Add((anchors[i], anchors[i + 1]));

        var combos = 1;
        foreach (var (a, b) in tieGroups)
        {
            var key = string.CompareOrdinal(a.AnchorId, b.AnchorId) <= 0
                ? $"{a.AnchorId}|{b.AnchorId}" : $"{b.AnchorId}|{a.AnchorId}";
            if (!st.TieCalls.ContainsKey(key)) combos *= 2;
        }

        if (combos > st.Parameters.MaxCandidates)
        {
            diags.Add(Diag.Error("ENG_CANDIDATE_BUDGET",
                $"Ambiguity yields {combos} legal tie orderings, exceeding the candidate budget {st.Parameters.MaxCandidates} (rule {st.Parameters.Version}).",
                null, "max_candidates"));
            return Outcome<IReadOnlyList<ModelCandidate>>.Budget(diags);
        }

        // 4) Expand tie-order combinations deterministically.
        var candidates = new List<ModelCandidate>();
        ExpandTies(st, anchors, tieGroups, 0,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new List<DerivedAnchor>(anchors),
            candidates);

        if (candidates.Count == 0)
        {
            diags.Add(Diag.Error("ENG_NO_LEGAL_MODEL",
                "No legal age-depth model can be assembled: every ordering violates an explicit constraint.",
                null, "structure"));
            return Outcome<IReadOnlyList<ModelCandidate>>.Unsolvable(diags);
        }

        // 5) Stable identity and deterministic ordering: equivalent inputs => same order.
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var genealogy = c.Choices.Count == 0 ? "base"
                : string.Join(";", c.Choices.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            var id = StableIds.Candidate(c.AnchorsInOrder, c.Choices, st.Parameters.Version);
            candidates[i] = c with { CandidateId = id, Genealogy = genealogy };
        }

        return Outcome<IReadOnlyList<ModelCandidate>>.Success(
            candidates.OrderBy(c => c.CandidateId, StringComparer.Ordinal).ToList());
    }

    private static void ExpandTies(
        ProjectState st,
        IReadOnlyList<DerivedAnchor> baseOrder,
        IList<(DerivedAnchor a, DerivedAnchor b)> ties,
        int idx,
        Dictionary<string, string> choices,
        List<DerivedAnchor> order,
        List<ModelCandidate> outCandidates)
    {
        if (idx == ties.Count)
        {
            var unresolved = ties.Select(t =>
                string.CompareOrdinal(t.a.AnchorId, t.b.AnchorId) <= 0
                    ? $"{t.a.AnchorId}|{t.b.AnchorId}" : $"{t.b.AnchorId}|{t.a.AnchorId}")
                .Where(k => !st.TieCalls.ContainsKey(k)).ToList();

            outCandidates.Add(new ModelCandidate(
                "", "",
                new Dictionary<string, string>(choices),
                order.ToList(),
                unresolved.Count > 0,
                unresolved,
                Array.Empty<Diagnostic>()));
            return;
        }

        var (a, b) = ties[idx];
        var tieKey = string.CompareOrdinal(a.AnchorId, b.AnchorId) <= 0
            ? $"{a.AnchorId}|{b.AnchorId}" : $"{b.AnchorId}|{a.AnchorId}";

        if (st.TieCalls.TryGetValue(tieKey, out var human))
        {
            choices[tieKey] = human.Order;
            ApplyTieOrder(order, a, b, human.Order);
            ExpandTies(st, baseOrder, ties, idx + 1, choices, order, outCandidates);
            return;
        }

        foreach (var ord in new[] { $"{a.AnchorId}<{b.AnchorId}", $"{b.AnchorId}<{a.AnchorId}" })
        {
            var subOrder = order.ToList();
            var subChoices = new Dictionary<string, string>(choices) { [tieKey] = ord };
            ApplyTieOrder(subOrder, a, b, ord);
            ExpandTies(st, baseOrder, ties, idx + 1, subChoices, subOrder, outCandidates);
        }
    }

    private static void ApplyTieOrder(List<DerivedAnchor> order, DerivedAnchor a, DerivedAnchor b, string ord)
    {
        var ia = order.FindIndex(x => x.AnchorId == a.AnchorId);
        var ib = order.FindIndex(x => x.AnchorId == b.AnchorId);
        if (ia < 0 || ib < 0 || Math.Abs(ia - ib) != 1) return;
        var first = ord.StartsWith(a.AnchorId, StringComparison.Ordinal) ? a : b;
        var second = ReferenceEquals(first, a) ? b : a;
        var lo = Math.Min(ia, ib);
        order[lo] = first;
        order[lo + 1] = second;
    }
}

public static class StableIds
{
    public static string ContentHash(string canonicalJson)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Short(string prefix, string canonicalJson)
    {
        var h = ContentHash(canonicalJson);
        return $"{prefix}_{h[..12]}";
    }

    public static string Candidate(IEnumerable<DerivedAnchor> anchors, IReadOnlyDictionary<string, string> choices, string ruleVersion)
    {
        var sb = new StringBuilder();
        sb.Append(ruleVersion).Append('|');
        foreach (var a in anchors)
            sb.Append(a.AnchorId).Append('@').Append(a.DepthM.ToString("R")).Append(';');
        sb.Append('|');
        foreach (var kv in choices.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return Short("CAND", sb.ToString());
    }
}
