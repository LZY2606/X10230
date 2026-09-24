using System.Security.Cryptography;
using System.Text;
using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Engine;

/// <summary>Engine output: all legal candidates, including equally optimal
/// ones, plus structured verdicts. "No unique answer" is represented by
/// retaining every legal candidate — never by collapsing to a mean.</summary>
public sealed record EngineResult(
    string InputDigest,
    string RulesetFingerprint,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Verdict> Verdicts,
    IReadOnlyList<TieGroupRecord> TieGroups);

public sealed record TieGroupRecord(string Key, IReadOnlyList<string> AnchorIds, bool Conflict);

public static class AgeModelEngine
{
    private static readonly double[] MonProbs = { 0.025, 0.5, 0.975 };

    public static string Digest(CorePackage pkg)
    {
        var sb = new StringBuilder()
            .Append(pkg.CaseId).Append('@').Append(pkg.CaseVersion).Append('\n')
            .Append(pkg.Datum.Id).Append(pkg.Datum.AxisUnit).Append(pkg.Datum.Direction)
            .Append(pkg.Datum.Raw).Append('\n');
        foreach (var s in pkg.Segments)
            sb.Append("S ").Append(s.Id).Append(s.Top.Raw).Append(s.Bottom.Raw).Append(s.Lithology).Append('\n');
        foreach (var b in pkg.Boundaries)
            sb.Append("B ").Append(b.Id).Append(b.Kind).Append(b.Depth.Raw).Append(b.Raw).Append('\n');
        foreach (var a in pkg.Anchors)
            sb.Append("A ").Append(a.MaterialKey).Append(a.Age.ReservoirRaw).Append('\n');
        foreach (var e in pkg.Events)
            sb.Append("E ").Append(e.Id).Append(e.CorrelationId).Append(e.Depth.Raw)
              .Append(e.TiedAge).Append(e.Raw).Append('\n');
        foreach (var x in pkg.Exclusions)
            sb.Append("X ").Append(x.Id).Append(x.TargetAnchorOrMaterialId).Append(x.ReasonCode).Append(x.Raw).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static EngineResult Solve(CorePackage pkg, Ruleset rules)
    {
        var geo = Geometry.Build(pkg);
        var verdicts = geo.Verdicts.ToList();
        var sets = CandidateEnumerator.Build(geo, pkg);
        var tieRecords = TieRecords(sets);

        var excludedAll = pkg.Exclusions.Select(e => e.TargetAnchorOrMaterialId).ToHashSet();
        var allAnchorIds = pkg.Anchors.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var scored = new List<(CandidateModel Model, double Score)>();
        var seen = new HashSet<string>();

        foreach (var admitted0 in CandidateEnumerator.Expand(sets))
        {
            var canonical = CandidateEnumerator.CanonicalKey(admitted0, rules.Fingerprint);
            if (!seen.Add(canonical)) continue;

            var curves = new Dictionary<string, SectionCurve>();
            var candidateVerdicts = new List<Verdict>();
            var feasible = true;

            foreach (var section in geo.Sections)
            {
                var inSection = admitted0
                    .Where(a => a.Depth >= section.Top.Value - Geometry.TieEpsilonCm &&
                                a.Depth <= section.Bottom.Value + Geometry.TieEpsilonCm)
                    .OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal)
                    .ToList();

                if (inSection.Count < 2)
                {
                    candidateVerdicts.Add(new("SECTION_UNDERCONSTRAINED", DiagSeverity.Warning,
                        $"连续段 {section.SectionId} 锚点不足 2 个，无法在段内唯一确定年龄，保留合法候选",
                        section.SegmentIds, null));
                    continue;
                }

                var violation = MonotonicViolation(inSection, section);
                if (violation is not null)
                {
                    feasible = false;
                    candidateVerdicts.Add(violation);
                    continue;
                }

                curves[section.SectionId] = Propagate(section, inSection, rules);
            }

            if (!feasible)
            {
                verdicts.AddRange(candidateVerdicts);
                continue;
            }

            var admittedIds = admitted0.Select(a => a.Id).ToList();
            var excludedIds = allAnchorIds.Where(id => !admittedIds.Contains(id) &&
                (excludedAll.Contains(id) || admitted0.Count > 0)).ToList();
            var tieIds = admitted0.Where(a =>
                sets.Any(s => s.Options.Any(o => o.Count > 1 && o.Any(z => z.Id == a.Id))))
                .Select(a => a.Id).ToList();

            var score = ScoreCandidate(pkg, inSectionAnchorIds: admitted0, rules, curves);
            var id = "CAND-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
            scored.Add((new CandidateModel(id, 0, false, score,
                admittedIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                excludedIds, tieIds, curves, candidateVerdicts, canonical), score));
        }

        if (scored.Count == 0)
        {
            verdicts.Add(new("NO_FEASIBLE_MODEL", DiagSeverity.SemanticUnsolvable,
                "没有任何候选能同时满足各连续段内的年龄单调约束（跨缺口/不整合不要求单调）",
                Array.Empty<string>(), null));
            return new EngineResult(Digest(pkg), rules.Fingerprint, Array.Empty<CandidateModel>(),
                verdicts, tieRecords);
        }

        // Deterministic, stable ordering: higher score, then lexicographic canonical key.
        var ordered = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Model.CanonicalKey, StringComparer.Ordinal)
            .ToList();
        var best = ordered[0].Score;
        const double tol = 1e-9;
        var models = ordered.Select((x, i) => x.Model with
        {
            Rank = i + 1,
            Optimal = x.Score >= best - tol
        }).ToList();

        if (models.Count(m => m.Optimal) > 1)
            verdicts.Add(new("MULTIPLE_OPTIMAL", DiagSeverity.Warning,
                $"存在 {models.Count(m => m.Optimal)} 个等优可辩护候选，全部保留，不做任意取舍",
                models.Where(m => m.Optimal).Select(m => m.Id).ToArray(), null));

        return new EngineResult(Digest(pkg), rules.Fingerprint, models, verdicts, tieRecords);
    }

    private static Verdict? MonotonicViolation(List<NormAnchor> inSection, ContinuousSection section)
    {
        for (var i = 1; i < inSection.Count; i++)
        {
            var prev = inSection[i - 1]; var cur = inSection[i];
            if (Math.Abs(cur.Depth - prev.Depth) <= Geometry.TieEpsilonCm) continue;
            // downward coring: age must not get younger with depth
            if (cur.Raw.Age.Q(0.5) + 1e-9 < prev.Raw.Age.Q(0.5))
                return new Verdict("AGE_INVERSION", DiagSeverity.SemanticUnsolvable,
                    $"连续段 {section.SectionId} 内年龄倒置：{cur.Id}({cur.Raw.Age.Raw}) 年轻于其上方 " +
                    $"{prev.Id}({prev.Raw.Age.Raw})；缺口/不整合之外不得倒置",
                    new[] { prev.Id, cur.Id }.Concat(section.SegmentIds).ToArray(), cur.Raw.SourceLine);
            // probabilistic crossing
            if (cur.Raw.Age.Q(0.975) < prev.Raw.Age.Q(0.025) - 1e-6)
                return new Verdict("AGE_INVERSION_STRONG", DiagSeverity.SemanticUnsolvable,
                    $"连续段 {section.SectionId} 内分布完全不交且倒置：{prev.Id} → {cur.Id}",
                    new[] { prev.Id, cur.Id }, cur.Raw.SourceLine);
        }
        return null;
    }

    private static SectionCurve Propagate(ContinuousSection section, List<NormAnchor> anchors, Ruleset rules)
    {
        var n = rules.GridPointsPerSection;
        var depths = new double[n];
        for (var i = 0; i < n; i++)
            depths[i] = section.Top.Value +
                (section.Bottom.Value - section.Top.Value) * i / (n - 1);

        var q025 = new double[n]; var q500 = new double[n]; var q975 = new double[n];
        var r025 = new double[n]; var r500 = new double[n]; var r975 = new double[n];
        var ds = anchors.Select(a => a.Depth).ToArray();

        var accumQ025 = new List<double>[n];
        var accumQ500 = new List<double>[n];
        var accumQ975 = new List<double>[n];
        var accumRLo = new List<double>[n];
        var accumRHi = new List<double>[n];
        var accumRMid = new List<double>[n];
        for (var i = 0; i < n; i++)
        {
            accumQ025[i] = new(); accumQ500[i] = new(); accumQ975[i] = new();
            accumRLo[i] = new(); accumRMid[i] = new(); accumRHi[i] = new();
        }

        for (var draw = 0; draw < rules.MonteCarloDraws; draw++)
        {
            var sampler = new Sampler(rules.RandomSeed + (uint)draw * 7919u);
            var ages = new double[anchors.Count];
            for (var i = 0; i < anchors.Count; i++)
            {
                var dist = anchors[i].Raw.Age;
                var raw = dist.Draw(sampler);
                ages[i] = raw - dist.ReservoirOffset;
            }
            // enforce non-decreasing order per draw (within section) by isotonic projection
            ages = IsotonicNonDecreasing(ages);

            for (var i = 0; i < n; i++)
            {
                var age = rules.Interpolation == InterpolationKind.Pchip
                    ? Pchip.Interpolate(ds, ages, depths[i])
                    : Linear(ds, ages, depths[i]);
                accumQ500[i].Add(age);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var col = accumQ500[i].OrderBy(x => x).ToArray();
            var qs = QuantileMath.QuantilesOf(col, MonProbs);
            q025[i] = qs[0]; q500[i] = qs[1]; q975[i] = qs[2];
            if (i == 0) { r025[i] = r500[i] = r975[i] = 0; continue;
            }
            var dd = depths[i] - depths[i - 1];
            r025[i] = dd / Math.Max(1e-9, q975[i] - q025[i - 1]);
            r975[i] = dd / Math.Max(1e-9, q025[i] - q975[i - 1]);
            r500[i] = dd / Math.Max(1e-9, q500[i] - q500[i - 1]);
            _ = (accumRLo, accumRHi, accumRMid);
        }
        return new SectionCurve(section.SectionId, depths, q025, q500, q975, r025, r500, r975);
    }

    private static double Linear(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = 1;
        while (xs[i] < x) i++;
        var f = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[i - 1] + f * (ys[i] - ys[i - 1]);
    }

    /// <summary>Pool-adjacent-violators: project per-draw ages to the closest
    /// non-decreasing sequence. Monotonicity holds only within the section the
    /// caller passes; section boundaries are never joined here.</summary>
    private static double[] IsotonicNonDecreasing(double[] v)
    {
        var n = v.Length;
        var val = new double[n];
        var len = new int[n];
        var k = -1;
        for (var i = 0; i < n; i++)
        {
            var cv = v[i]; var cl = 1;
            while (k >= 0 && val[k] > cv)
            {
                cv = (val[k] * len[k] + cv * cl) / (len[k] + cl);
                cl += len[k];
                k--;
            }
            k++; val[k] = cv; len[k] = cl;
        }
        var outp = new double[n];
        var idx = n - 1;
        while (k >= 0)
        {
            for (var j = 0; j < len[k]; j++) outp[idx--] = val[k];
            k--;
        }
        return outp;
    }

    private static double ScoreCandidate(CorePackage pkg, IReadOnlyList<NormAnchor> inSectionAnchorIds,
        Ruleset rules, IReadOnlyDictionary<string, SectionCurve> curves)
    {
        var n = inSectionAnchorIds.Count;
        // Event-layer corroboration: tied event ages that fall within uncertainty add score
        var eventHits = 0;
        var anchorDepths = inSectionAnchorIds.Select(a => a.Depth).ToHashSet();
        foreach (var ev in pkg.Events)
        {
            if (!UnitSystem.IsDepthConvertible(ev.Depth) || ev.TiedAge is null) continue;
            var d = UnitSystem.ToCm(ev.Depth);
            foreach (var c in curves.Values)
            {
                var idx = Array.BinarySearch(c.DepthsCm, d);
                if (idx < 0) continue;
                if (ev.TiedAge >= c.Q025[idx] && ev.TiedAge <= c.Q975[idx]) eventHits++;
            }
        }
        _ = anchorDepths;
        // Prefer corroborated, well-constrained models; parsimony breaks only ties weakly.
        return n * 1.0 + eventHits * rules.EventTieWeight - rules.ParsimonyWeight * 0;
    }

    private static IReadOnlyList<TieGroupRecord> TieRecords(IReadOnlyList<CandidateEnumerator.ChoiceSet> sets)
    {
        var recs = new List<TieGroupRecord>();
        foreach (var s in sets)
        {
            var ids = s.Options.SelectMany(o => o.Select(a => a.Id)).Distinct().OrderBy(x => x).ToList();
            if (ids.Count > 1)
                recs.Add(new TieGroupRecord(s.Signature, ids, s.Options.Count > 1));
        }
        return recs;
    }
}
