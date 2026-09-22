using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StratAge.Core.Domain;

namespace StratAge.Core.Engine;

/// <summary>
/// Server-side, storage-independent derivation engine. Inputs are an immutable
/// material set, the active rule-set version and the set of decision effects
/// (exclusions / tie choices / accepted inversions) reached on a branch.
/// </summary>
public sealed class AgeModelEngine
{
    const int GridPointsPerSection = 12;

    public ModelDerivation Build(MaterialSet material, RuleSetVersion rule, DecisionEffects effects)
    {
        var diag = new List<Diagnostic>();

        // 1. project exclusions / reinstatements onto anchors
        var anchors = ProjectAnchors(material, effects, diag);

        // 2. unconvertible material is retained but diagnosed with position
        foreach (var a in material.Of<AnchorAge>())
        {
            if (!a.Depth.IsConvertible) diag.Add(Diagnostic.Err("E-DEPTH-UNIT",
                $"anchor {a.Id}: {a.Depth.ConversionDiagnostic}; value retained at original position",
                a.Pos, a.Id));
            if (!a.Age.IsConvertible) diag.Add(Diagnostic.Err("E-AGE-UNIT",
                $"anchor {a.Id}: {a.Age.ConversionDiagnostic}; value retained at original position",
                a.Pos, a.Id));
        }

        // 3. explicit section/gap boundaries
        var sections = BuildSections(material, anchors, effects, diag);

        // 4. monotonicity audit inside each un-faulted section
        AuditMonotonicity(sections, diag);

        // 5. candidate generation under budget
        var (candidates, used, exhausted) = GenerateCandidates(material, rule, sections, diag);

        // 6. rank + keep all equally-optimal candidates
        Rank(candidates);

        var status = !candidates.Any(c => c.Feasible)
            ? DerivationStatus.SemanticImpossible
            : exhausted ? DerivationStatus.BudgetExhausted : DerivationStatus.Ok;
        if (status == DerivationStatus.SemanticImpossible)
            diag.Add(Diagnostic.Err("E-NO-FEASIBLE",
                "no candidate satisfies monotonic ageing in any un-faulted section", null,
                anchors.Select(a => a.AnchorId).ToArray()));
        if (exhausted)
            diag.Add(Diagnostic.Warn("W-BUDGET",
                $"candidate budget {rule.CandidateBudget} exhausted before enumeration finished", null,
                Array.Empty<string>()));

        var digest = Digest(material, rule, effects);
        return new ModelDerivation
        {
            Status = status, Material = material, Rule = rule, Diagnostics = diag,
            Anchors = anchors, Sections = sections, Candidates = candidates,
            BudgetUsed = used, BudgetLimit = rule.CandidateBudget, InputDigest = digest
        };
    }

    // ---------------------------------------------------------------- anchors

    List<ResolvedAnchor> ProjectAnchors(MaterialSet m, DecisionEffects fx, List<Diagnostic> diag)
    {
        var excluded = new HashSet<string>(m.Of<Exclusion>().Select(e => e.TargetId));
        foreach (var eid in fx.Reinstated) excluded.Remove(eid);
        foreach (var eid in fx.AdditionalExclusions) excluded.Add(eid);

        var offsets = m.Of<ReservoirOffset>().ToDictionary(o => o.Id);
        var raw = new List<(AnchorAge a, double[] draws, double? mean)>();
        foreach (var a in m.Of<AnchorAge>().OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (!a.Depth.IsConvertible || !a.Age.IsConvertible)
            {
                raw.Add((a, Array.Empty<double>(), null));
                continue;
            }
            var seed = StableSeed(a.Id);
            var draws = Quantiles.Sample(a.Shape, a.Param1, a.Param2, a.Param3,
                m.Of<RuleSetVersion>().Any() ? 0 : 0, seed); // draws count set below via rule
            raw.Add((a, draws, Quantiles.Mean(draws)));
        }
        return raw.Select(t =>
        {
            var (a, draws0, mean) = t;
            double[] draws = Array.Empty<double>();
            if (draws0.Length > 0)
            {
                draws = draws0;
                if (!string.IsNullOrEmpty(a.ReservoirOffsetId) &&
                    offsets.TryGetValue(a.ReservoirOffsetId!, out var off))
                    draws = Quantiles.ApplyOffset(draws, off.Shape, off.Param1, off.Param2,
                        StableSeed(a.Id + "|R"));
            }
            var isEx = excluded.Contains(a.Id);
            string? reason = null;
            if (isEx)
                reason = m.Of<Exclusion>().FirstOrDefault(e => e.TargetId == a.Id)?.Reason
                         ?? fx.AdditionalExclusionReasons.GetValueOrDefault(a.Id);
            return new ResolvedAnchor(a.Id, a.Depth.Canonical ?? double.NaN, a.Depth, a.Age,
                draws,
                draws.Length == 0 ? double.NaN : Quantiles.Quantile(draws, 0.5),
                draws.Length == 0 ? double.NaN : Quantiles.Quantile(draws, 0.025),
                draws.Length == 0 ? double.NaN : Quantiles.Quantile(draws, 0.975),
                mean, a.ReservoirOffsetId, isEx, reason, false, -1, -1, -1);
        }).ToList();
    }

    // -------------------------------------------------------------- sections

    List<SectionModel> BuildSections(MaterialSet m, List<ResolvedAnchor> anchors0,
        DecisionEffects fx, List<Diagnostic> diag)
    {
        var usable = anchors0.Where(a => !a.Excluded && a.DepthDrawsValid).ToList();

        // explicit gaps / unconformities are boundaries
        var gaps = m.Of<Gap>()
            .Where(g => g.Top.IsConvertible && g.Bottom.IsConvertible)
            .OrderBy(g => g.Top.Canonical!.Value)
            .ToList();
        foreach (var g in m.Of<Gap>().Where(g => !g.Top.IsConvertible || !g.Bottom.IsConvertible))
            diag.Add(Diagnostic.Err("E-GAP-UNIT",
                $"gap {g.Id}: boundary depth not convertible; retained, treated as hard barrier",
                g.Pos, g.Id));

        // inverted segments are boundaries too, but marked structurally inverted
        var segs = m.Of<Segment>().OrderBy(s => s.Top.Canonical ?? double.MaxValue).ToList();
        var invertedDepths = new List<(double top, double bottom, string id)>();
        foreach (var s in segs.Where(s => s.Inverted))
        {
            if (s.Top.IsConvertible && s.Bottom.IsConvertible)
                invertedDepths.Add((s.Top.Canonical!.Value, s.Bottom.Canonical!.Value, s.Id));
        }

        // depth duplicates: cluster equal-depth usable anchors into tie clusters
        var byDepth = usable
            .OrderBy(a => a.DepthMbsf).ThenBy(a => a.AnchorId, StringComparer.Ordinal)
            .GroupBy(a => a.DepthMbsf)
            .ToList();

        var sections = new List<SectionModel>();
        var sectionIdx = 0;
        double cursor = usable.Count == 0 ? 0 : usable.Min(a => a.DepthMbsf);
        var bottomAll = usable.Count == 0 ? 0 : usable.Max(a => a.DepthMbsf);

        // merge gaps + inverted segment boundaries into ordered barriers
        var barriers = gaps
            .Select(g => (g.Top.Canonical!.Value, g.Bottom.Canonical!.Value, reason: $"{g.GapType}: {g.Basis}", gap: true, id: g.Id))
            .Concat(invertedDepths.Select(x => (x.top, x.bottom, reason: $"inverted segment {x.id}", gap: false, id: x.id)))
            .OrderBy(t => t.Item1)
            .ToList();

        if (usable.Count == 0)
        {
            diag.Add(Diagnostic.Err("E-NO-ANCHOR",
                "no usable anchor with convertible depth+age; cannot build any depth-age section", null,
                Array.Empty<string>()));
        }

        foreach (var (bTop, bBot, reason, isGap, bid) in barriers)
        {
            if (bTop > cursor)
            {
                var inside = usable.Where(a => a.DepthMbsf >= cursor - 1e-9 && a.DepthMbsf <= bTop + 1e-9).ToList();
                sections.Add(MakeSection(sectionIdx++, cursor, bTop, false, null, inside, fx, diag));
            }
            sections.Add(MakeSection(sectionIdx++, bTop, bBot, isGap, reason,
                usable.Where(a => a.DepthMbsf > bTop && a.DepthMbsf < bBot).ToList(), fx, diag));
            cursor = bBot;
        }
        if (cursor <= bottomAll || sections.Count == 0)
        {
            var inside = usable.Where(a => a.DepthMbsf >= cursor - 1e-9).ToList();
            if (inside.Count > 0 || sections.Count == 0)
                sections.Add(MakeSection(sectionIdx, cursor, bottomAll, false, null, inside, fx, diag));
        }
        return sections;
    }

    SectionModel MakeSection(int idx, double top, double bottom, bool isGap, string? reason,
        List<ResolvedAnchor> anchors, DecisionEffects fx, List<Diagnostic> diag)
    {
        // assign tie clusters + ranks
        var grouped = anchors
            .OrderBy(a => a.DepthMbsf)
            .ThenBy(a => a.AnchorId, StringComparer.Ordinal)
            .GroupBy(a => a.DepthMbsf)
            .OrderBy(g => g.Key)
            .ToList();
        var resolved = new List<ResolvedAnchor>();
        var cluster = 0;
        foreach (var g in grouped)
        {
            var members = g.OrderBy(a => a.AnchorId, StringComparer.Ordinal).ToList();
            // apply a recorded tie ordering if present
            if (fx.TieOrderings.TryGetValue(g.Key, out var order))
                members = members.OrderBy(a => order.IndexOf(a.AnchorId) < 0
                    ? int.MaxValue : order.IndexOf(a.AnchorId)).ToList();
            var rank = 0;
            foreach (var a in members)
                resolved.Add(a with { SectionIndex = idx, TieCluster = cluster, TieRank = rank++, InvertedSection = !isGap && reason is not null });
            cluster++;
        }
        if (isGap && anchors.Count > 0)
            diag.Add(Diagnostic.Warn("W-ANCHOR-IN-GAP",
                $"anchors inside declared gap/unconformity {bottom - top:0.######} mbsf are excluded from interpolation",
                null, anchors.Select(a => a.AnchorId).ToArray()));
        return new SectionModel(idx, top, bottom, isGap, reason, resolved);
    }

    void AuditMonotonicity(List<SectionModel> sections, List<Diagnostic> diag)
    {
        foreach (var s in sections)
        {
            if (s.IsGap) continue;
            var depths = s.Anchors.Select(a => a.DepthMbsf).Distinct().OrderBy(x => x).ToList();
            for (var i = 1; i < depths.Count; i++)
            {
                var up = s.Anchors.Where(a => a.DepthMbsf == depths[i - 1]).ToList();
                var dn = s.Anchors.Where(a => a.DepthMbsf == depths[i]).ToList();
                foreach (var u in up) foreach (var d in dn)
                {
                    var frac = Quantiles.InversionFraction(u.AgeDraws, d.AgeDraws);
                    if (frac > 0.5)
                        diag.Add(Diagnostic.Err("E-INVERSION",
                            $"age inversion: {u.AnchorId}->{d.AnchorId} older-above in {frac:P0} of draws; " +
                            "un-faulted sections must age monotonically with depth",
                            null, new[] { u.AnchorId, d.AnchorId }));
                    else if (frac > 0.025)
                        diag.Add(Diagnostic.Warn("W-SOFT-INVERSION",
                            $"soft overlap: {u.AnchorId}->{d.AnchorId} inversion in {frac:P1} of draws",
                            null, new[] { u.AnchorId, d.AnchorId }));
                }
            }
        }
    }

    // ------------------------------------------------------------- candidates

    (List<CandidateModel> cand, int used, bool exhausted) GenerateCandidates(
        MaterialSet material, RuleSetVersion rule, List<SectionModel> sections, List<Diagnostic> diag)
    {
        var cands = new List<CandidateModel>();
        var budget = rule.CandidateBudget;
        var used = 0;
        var exhausted = false;

        // tie groups across un-faulted sections: depth -> anchor ids (size>1)
        var tieGroups = sections
            .Where(s => s.BoundaryReason is null)
            .SelectMany(s => s.Anchors.Where(a => a.AgeDraws.Length > 0)
                .GroupBy(a => a.DepthMbsf).Where(g => g.Count() > 1)
                .Select(g => g.Select(a => a.AnchorId).OrderBy(x => x, StringComparer.Ordinal).ToArray()))
            .OrderBy(g => string.Join("|", g), StringComparer.Ordinal)
            .ToArray();

        foreach (var order in EnumerateOrderings(tieGroups))
        {
            var tieMap = new Dictionary<double, List<string>>();
            // rebuild tie signature from permutation
            var sig = string.Join(";", order.Select(p => string.Join(">", p)));

            foreach (var method in new[] { "linear", "isotonic" })
            {
                if (used >= budget) { exhausted = true; break; }
                used++;
                cands.Add(BuildCandidate(method, order, sig, sections, rule));
            }
            if (exhausted) break;
        }
        if (cands.Count == 0)
        {
            // no usable anchors at all
            used = 0;
        }
        return (cands, used, exhausted);
    }

    static IEnumerable<string[][]> EnumerateOrderings(string[][] groups)
    {
        if (groups.Length == 0) { yield return Array.Empty<string[]>(); yield break; }
        IEnumerable<string[]> rec(int i)
        {
            var perms = Permutations(groups[i]);
            if (i == groups.Length - 1) foreach (var p in perms) yield return p;
            else foreach (var tail in rec(i + 1))
                foreach (var p in perms)
                {
                    var comb = new string[groups.Length - i];
                    comb[0] = p; Array.Copy(tail, 0, comb, 1, tail.Length);
                    yield return comb;
                }
        }
        foreach (var c in rec(0))
        {
            var full = new string[groups.Length][];
            for (var i = 0; i < groups.Length; i++)
                full[i] = c.Skip(i).First();
            yield return full;
        }
    }

    static IEnumerable<string[]> Permutations(string[] xs)
    {
        if (xs.Length <= 1) { yield return xs; yield break; }
        foreach (var x in xs)
            foreach (var rest in Permutations(xs.Where(y => y != x).ToArray()))
                yield return new[] { x }.Concat(rest).ToArray();
    }

    CandidateModel BuildCandidate(string method, string[][] tieOrder, string tieSig,
        List<SectionModel> sections, RuleSetVersion rule)
    {
        var cdiag = new List<Diagnostic>();
        var tieMap = BuildTieMap(sections, tieOrder);
        var curve = new List<CurvePoint>();
        var rates = new List<RateInterval>();
        var feasible = true;
        var permId = StablePermId(tieSig);
        var cid = $"cand-{method[..3]}-{permId}";

        foreach (var s in sections.Where(x => x.BoundaryReason is null))
        {
            var ordered = OrderSection(s, tieMap);
            if (ordered.Count == 0) continue;
            if (ordered.Count == 1)
                cdiag.Add(Diagnostic.Warn("W-SECTION-UNCONSTRAINED",
                    $"section {s.Index} has one anchor; ages defined only at the point, no rate",
                    null, ordered.Select(a => a.AnchorId).ToArray()));

            // duplicate depth rows with different ages after tie order
            var nodes = ordered;
            if (method == "linear" && !LinearFeasible(nodes, cdiag)) feasible = false;

            var draws = rule.MonteCarloDraws;
            var n = nodes.Count;
            var perDrawAge = new double[draws][];
            for (var r = 0; r < draws; r++)
            {
                var ages = new double[n];
                for (var i = 0; i < n; i++) ages[i] = nodes[i].AgeDraws[r % nodes[i].AgeDraws.Length];
                if (method == "isotonic") ages = Pava(nodes.Select(x => x.DepthMbsf).ToArray(), ages);
                perDrawAge[r] = ages;
            }

            // feasibility for isotonic: always monotonic by construction; linear checked above
            if (!feasible) continue;

            // grid points
            var d0 = nodes.First().DepthMbsf; var d1 = nodes.Last().DepthMbsf;
            for (var g = 0; g <= GridPointsPerSection; g++)
            {
                var d = d0 + (d1 - d0) * g / GridPointsPerSection;
                var vals = new double[draws];
                for (var r = 0; r < draws; r++)
                    vals[r] = Interp(nodes.Select(x => x.DepthMbsf).ToArray(), perDrawAge[r], d);
                curve.Add(new CurvePoint($"curve:{cid}:s{s.Index}:g{g:00}", d,
                    Quantiles.Quantile(vals, 0.5), Quantiles.Quantile(vals, 0.025),
                    Quantiles.Quantile(vals, 0.975), s.Index));
            }
            // rates between successive distinct depths
            for (var i = 1; i < n; i++)
            {
                var dd = nodes[i].DepthMbsf - nodes[i - 1].DepthMbsf;
                if (dd <= 1e-12) continue;
                var rv = new double[draws];
                for (var r = 0; r < draws; r++)
                {
                    var da = perDrawAge[r][i] - perDrawAge[r][i - 1];
                    rv[r] = da <= 0 ? 0 : dd / da;
                }
                rates.Add(new RateInterval($"rate:{cid}:s{s.Index}:i{i}",
                    nodes[i - 1].DepthMbsf, nodes[i].DepthMbsf,
                    Quantiles.Quantile(rv, 0.5), Quantiles.Quantile(rv, 0.025),
                    Quantiles.Quantile(rv, 0.975), s.Index));
            }
        }

        var band = curve.Count == 0 ? double.PositiveInfinity : curve.Sum(p => p.HighKa - p.LowKa);
        var rough = RateRoughness(rates);
        return new CandidateModel
        {
            StableId = cid,
            Method = method,
            MethodLabel = method == "linear" ? "piecewise linear interpolation" : "isotonic regression (PAVA)",
            TieSignature = tieSig,
            Curve = curve, Rates = rates,
            Feasible = feasible && curve.Count > 0,
            BandWidthKa = band, FitPenalty = rough,
            Score = band + rough,
            Diagnostics = cdiag
        };
    }

    static Dictionary<double, List<string>> BuildTieMap(List<SectionModel> sections, string[][] tieOrder)
    {
        var map = new Dictionary<double, List<string>>();
        if (tieOrder.Length == 0) return map;
        var k = 0;
        foreach (var s in sections.Where(x => x.BoundaryReason is null))
            foreach (var g in s.Anchors.GroupBy(a => a.DepthMbsf).Where(g => g.Count() > 1)
                         .OrderBy(g => g.Key))
            {
                if (k < tieOrder.Length) map[g.Key] = tieOrder[k++].ToList();
            }
        return map;
    }

    static List<ResolvedAnchor> OrderSection(SectionModel s, Dictionary<double, List<string>> tieMap)
    {
        return s.Anchors.Where(a => a.AgeDraws.Length > 0)
            .OrderBy(a => a.DepthMbsf)
            .ThenBy(a => tieMap.TryGetValue(a.DepthMbsf, out var ord)
                ? ord.IndexOf(a.AnchorId) < 0 ? int.MaxValue : ord.IndexOf(a.AnchorId)
                : 0)
            .ThenBy(a => a.AnchorId, StringComparer.Ordinal)
            .ToList();
    }

    static bool LinearFeasible(List<ResolvedAnchor> nodes, List<Diagnostic> diag)
    {
        var ok = true;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].DepthMbsf <= nodes[i - 1].DepthMbsf + 1e-12) continue; // tie row
            var frac = Quantiles.InversionFraction(nodes[i - 1].AgeDraws, nodes[i].AgeDraws);
            if (frac > 0.5)
            {
                ok = false;
                diag.Add(Diagnostic.Err("E-LINEAR-INVERSION",
                    $"linear candidate infeasible between {nodes[i - 1].AnchorId} and {nodes[i].AnchorId}: " +
                    $"older-above in {frac:P0} of draws", null,
                    new[] { nodes[i - 1].AnchorId, nodes[i].AnchorId }));
            }
        }
        return ok;
    }

    /// <summary>Pool-adjacent-violators on weighted (depth) means, per Monte Carlo draw.</summary>
    static double[] Pava(double[] depth, double[] age)
    {
        var n = age.Length;
        var blocks = new List<(double w, double sum, List<int> idx)>();
        for (var i = 0; i < n; i++)
        {
            var wb = new List<int> { i };
            blocks.Add((depth[i] - (i > 0 ? depth[i - 1] : depth[i] - 1), age[i] * 1, wb));
            while (blocks.Count >= 2)
            {
                var (w1, s1, i1) = blocks[^2];
                var (w2, s2, i2) = blocks[^1];
                if (s1 / w1 <= s2 / w2 + 1e-9) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks.RemoveAt(blocks.Count - 1);
                i1.AddRange(i2);
                blocks.Add((w1 + w2, s1 + s2, i1));
            }
        }
        var outp = new double[n];
        foreach (var (w, sum, idx) in blocks)
            foreach (var i in idx) outp[i] = sum / w;
        return outp;
    }

    static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                var f = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + f * (ys[i] - ys[i - 1]);
            }
        }
        return ys[^1];
    }

    static double RateRoughness(List<RateInterval> rates)
    {
        double r = 0;
        for (var i = 1; i < rates.Count; i++)
            r += Math.Abs(rates[i].MedianMPerKa - rates[i - 1].MedianMPerKa);
        return r;
    }

    static void Rank(List<CandidateModel> cands)
    {
        var feas = cands.Where(c => c.Feasible).ToList();
        if (feas.Count == 0) return;
        // Pareto on (band width, rate roughness)
        foreach (var c in feas)
            c.ParetoOptimal = feas.All(o => ReferenceEquals(o, c) ||
                o.BandWidthKa > c.BandWidthKa + 1e-9 || o.FitPenalty > c.FitPenalty + 1e-9);
        var best = feas.Min(c => c.Score);
        foreach (var c in feas)
            c.EquallyOptimal = Math.Abs(c.Score - best) <= 1e-6;
    }

    static ulong StableSeed(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("stratage/v1|" + s));
        return BitConverter.ToUInt64(h, 0);
    }

    static string StablePermId(string sig)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("perm|" + sig));
        return Convert.ToHexString(h)[..8].ToLowerInvariant();
    }

    public static string Digest(MaterialSet material, RuleSetVersion rule, DecisionEffects effects)
    {
        var sb = new StringBuilder();
        sb.Append(rule.Id).Append('@').Append(rule.Version).Append('\n');
        foreach (var m in material.Items.Where(x => !x.Retracted)
                     .OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append(m.Id).Append('=').Append(m.Kind).Append(':').Append(m.GetHashCode()).Append('\n');
        sb.Append(effects.DigestFragment());
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(h)[..16].ToLowerInvariant();
    }
}
