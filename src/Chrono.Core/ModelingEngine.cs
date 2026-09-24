using System.Text;
using System.Text.Json;

namespace Chrono.Core;

/// <summary>
/// Server-side depth-age modelling engine. Everything a chart shows comes from here: the browser
/// never owns the monotonicity, gap, reservoir or multi-optima rules.
/// </summary>
public sealed class ModelingEngine
{
    private const double DepthToleranceM = 0.002; // 2 mm: depths closer than this are co-located

    private readonly ParameterSet _p;
    public ModelingEngine(ParameterSet? parameters = null) => _p = parameters ?? ParameterSet.V1();

    public DerivationResult Derive(MaterialSet materials, DeriveContext ctx)
    {
        var diagnostics = new List<DeriveDiagnostic>();
        var findings = new List<Finding>();

        var digest = ComputeDigest(materials, ctx);
        var seed = Convert.ToUInt64(digest[..16], 16);
        var rng = new DeterministicRng(seed);

        // --- 1. completeness: incomplete input is reported with locations, never faked with defaults
        if (!CheckCompleteness(materials, diagnostics, out var fatalIncomplete))
        {
            return Finish(ctx, digest, DeriveStatus.IncompleteInput, diagnostics, findings,
                Array.Empty<CandidateModel>(), 0, false);
        }

        // --- 2. effective anchors: depth converted, reservoir offsets applied to distributions only
        var effective = BuildEffectiveAnchors(materials, diagnostics, findings);

        // --- 3. sections: gaps/unconformities are explicit boundaries, never interpolated across
        var sections = BuildSections(materials);

        // --- 4. locate anchors in sections; detect duplicated depths and inversions
        var bySection = AssignAnchors(effective, sections, materials, diagnostics, findings);

        // --- 5. enumerate defensible variants from co-located groups, respecting the budget
        var variants = EnumerateVariants(bySection, diagnostics, findings, out var budgetExhausted);

        var candidates = new List<CandidateModel>();
        var drawsUsed = 0;
        foreach (var variant in variants)
        {
            var model = BuildCandidate(variant, sections, effective, materials, rng, ctx, ref drawsUsed);
            candidates.Add(model);
        }

        // score + rank; ties are retained (multiple equally optimal models)
        candidates = RankCandidates(candidates, findings);

        var status = candidates.Any(c => c.Sections.Any(s => s.MonotonicValid))
            ? (budgetExhausted ? DeriveStatus.BudgetExhausted : DeriveStatus.Ok)
            : DeriveStatus.SemanticUnsolvable;

        if (status == DeriveStatus.SemanticUnsolvable)
            findings.Add(new Finding(FindingKind.SemanticUnsolvable,
                "No candidate satisfies within-section monotonicity at every active anchor.",
                effective.Select(a => a.Id).ToList(),
                new Dictionary<string, string> { ["rule"] = _p.VersionId }));

        return Finish(ctx, digest, status, diagnostics, findings, candidates, drawsUsed, budgetExhausted);
    }

    private DerivationResult Finish(DeriveContext ctx, string digest, DeriveStatus status,
        List<DeriveDiagnostic> diagnostics, List<Finding> findings,
        IReadOnlyList<CandidateModel> candidates, int drawsUsed, bool budgetExhausted) =>
        new()
        {
            ResultId = StableId("result", digest + "|" + _p.VersionId + "|" + string.Join(",", ctx.Parents)),
            InputDigest = digest,
            ParameterVersion = _p.VersionId,
            ParentResultIds = ctx.Parents,
            MaterialRevisionId = ctx.MaterialRevisionId,
            Status = status,
            Diagnostics = diagnostics,
            Findings = findings,
            Candidates = candidates,
            DrawsUsed = drawsUsed,
            BudgetExhausted = budgetExhausted
        };

    public static string StableId(string prefix, string content) => $"{prefix}-{Hashing.Sha256Hex(content)[..12]}";

    // ---------------------------------------------------------------- completeness
    private bool CheckCompleteness(MaterialSet m, List<DeriveDiagnostic> d, out bool fatal)
    {
        fatal = false;
        if (string.IsNullOrEmpty(m.Datum.Id))
        {
            d.Add(new("INCOMPLETE", "error", "$.datum", "Depth datum is missing.", Array.Empty<string>()));
            fatal = true;
        }
        if (m.Segments.Count == 0)
        {
            d.Add(new("INCOMPLETE", "error", "$.segments", "No core segments defined.", Array.Empty<string>()));
            fatal = true;
        }
        foreach (var a in m.Anchors)
        {
            var loc = $"$.anchors[{a.Id}]";
            if (!a.Depth.IsConverted)
                d.Add(new("INCOMPLETE", "error", loc + ".depth",
                    $"Anchor {a.Id} depth not usable: {a.Depth.UnconvertedReason} (raw '{a.Depth.Raw}' kept)",
                    new[] { a.Id }));
            if (!a.Excluded && a.Distribution.SamplesCalBP.Length < 8 &&
                (a.Distribution.MuCalBP is null || a.Distribution.SigmaCalBP is null) &&
                a.Distribution.SampleType != AgeSampleType.Unsupported)
                d.Add(new("INCOMPLETE", "error", loc + ".distribution",
                    $"Anchor {a.Id} has neither >=8 empirical samples nor (mu,sigma); distribution cannot be invented.",
                    new[] { a.Id }));
        }
        return !fatal;
    }

    // ---------------------------------------------------------------- effective anchors (offset only shifts distribution)
    private List<EffectiveAnchor> BuildEffectiveAnchors(MaterialSet m,
        List<DeriveDiagnostic> diagnostics, List<Finding> findings)
    {
        var list = new List<EffectiveAnchor>();
        foreach (var a in m.ActiveAnchors)
        {
            var samples = SampleDistribution(a, m, diagnostics);
            if (!string.IsNullOrEmpty(a.ReservoirOffsetId))
            {
                var ro = m.ReservoirOffsets.FirstOrDefault(r => r.Id == a.ReservoirOffsetId);
                if (ro is null)
                {
                    diagnostics.Add(new("MISSING_OFFSET", "error", $"$.anchors[{a.Id}].reservoirOffsetId",
                        $"Reservoir offset {a.ReservoirOffsetId} referenced by {a.Id} does not exist.", new[] { a.Id }));
                }
                else
                {
                    var dr = ro.DeltaR.Normalized;
                    var sr = ro.SigmaR.Normalized ?? 0;
                    if (dr is double shift)
                    {
                        // calibrated calendar age = measured (with apparent reservoir age) minus local correction
                        var rng = new DeterministicRng(Convert.ToUInt64(Hashing.Sha256Hex("ro|" + a.Id)[..16], 16));
                        samples = samples.Select(s => s - shift + rng.Normal() * sr).ToArray();
                    }
                }
            }
            Array.Sort(samples);
            if (a.Distribution.LongTailPpm is int ppm and > 0)
                findings.Add(new Finding(FindingKind.LongTailDistribution,
                    $"Anchor {a.Id} carries a long-tail distribution (~{ppm} ppm tail); median-only summary refused.",
                    new[] { a.Id }, new Dictionary<string, string>
                    { ["samples"] = samples.Length.ToString(), ["p05"] = F(Q(samples, .05)), ["p95"] = F(Q(samples, .95)) }));
            list.Add(new EffectiveAnchor(a.Id, a.Depth.Normalized!.Value, samples, a));
        }
        return list;
    }

    private double[] SampleDistribution(AgeAnchor a, MaterialSet m, List<DeriveDiagnostic> diagnostics)
    {
        var d = a.Distribution;
        var key = $"dist|{a.Id}|{m.RevisionId}|{_p.VersionId}|{_p.MonteCarloDraws}";
        var rng = new DeterministicRng(Convert.ToUInt64(Hashing.Sha256Hex(key)[..16], 16));
        var n = _p.MonteCarloDraws;
        if (d.SamplesCalBP.Length >= 8)
        {
            // resample empirical set with deterministic bootstrap
            var src = (double[])d.SamplesCalBP.Clone();
            Array.Sort(src);
            var outp = new double[n];
            for (var i = 0; i < n; i++) outp[i] = src[rng.NextU64() % (ulong)src.Length];
            return outp;
        }
        if (d.MuCalBP is double mu && d.SigmaCalBP is double sg)
        {
            var outp = new double[n];
            for (var i = 0; i < n; i++)
                outp[i] = d.Skew is double sk && Math.Abs(sk) > 1e-9
                    ? rng.SkewNormal(mu, sg, sk) : mu + rng.Normal() * sg;
            return outp;
        }
        diagnostics.Add(new("UNDISTRIBUTED", "error", $"$.anchors[{a.Id}]",
            $"Anchor {a.Id} ({d.SampleType}) has no usable distribution; it stays in place but cannot enter a model.",
            new[] { a.Id }));
        return Array.Empty<double>();
    }

    private record EffectiveAnchor(string Id, double DepthM, double[] Samples, AgeAnchor Source);

    // ---------------------------------------------------------------- sections from segments
    private List<SectionSlot> BuildSections(MaterialSet m)
    {
        var slots = new List<SectionSlot>();
        var ordered = m.Segments.OrderBy(s => s.TopDepthM).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var topB = i == 0 ? "core-top" : Boundary(ordered[i - 1].BaseRelation);
            var botB = i == ordered.Count - 1 ? "core-base" : Boundary(ordered[i].BaseRelation);
            slots.Add(new SectionSlot(i, ordered[i].TopDepthM, ordered[i].BaseDepthM, topB, botB,
                ordered[i].Id));
        }
        return slots;
    }

    private static string Boundary(SegmentRelation r) => r switch
    {
        SegmentRelation.Unconformity => "unconformity",
        SegmentRelation.CoreGap => "gap",
        _ => "continuous"
    };

    private record SectionSlot(int Index, double Top, double Base, string TopB, string BotB, string SegmentId)
    {
        public List<EffectiveAnchor> Anchors { get; } = new();
        public bool Blocked { get; set; }
        public string? BlockReason { get; set; }
    }

    // ---------------------------------------------------------------- assignment + structural checks
    private List<SectionSlot> AssignAnchors(List<EffectiveAnchor> anchors, List<SectionSlot> sections,
        MaterialSet m, List<DeriveDiagnostic> diagnostics, List<Finding> findings)
    {
        foreach (var sec in sections)
        {
            sec.Anchors.AddRange(anchors.Where(a => a.DepthM >= sec.Top - DepthToleranceM
                                                 && a.DepthM <= sec.Base + DepthToleranceM)
                                       .OrderBy(a => a.DepthM).ThenBy(a => a.Id, StringComparer.Ordinal));

            // explicit boundary findings
            if (sec.BotB is "gap")
                findings.Add(new Finding(FindingKind.GapBoundary,
                    $"Segment {sec.SegmentId} ends in a core gap; no interpolation crosses it.",
                    new[] { sec.SegmentId }, new Dictionary<string,string> { ["baseDepthM"] = F(sec.Base), ["boundary"] = "gap" }));
            if (sec.BotB is "unconformity")
                findings.Add(new Finding(FindingKind.UnconformityBoundary,
                    $"Segment {sec.SegmentId} ends in an unconformity; an explicit time break, not a fast interval.",
                    new[] { sec.SegmentId }, new Dictionary<string,string> { ["baseDepthM"] = F(sec.Base), ["boundary"] = "unconformity" }));

            DetectDuplicatesAndInversion(sec, diagnostics, findings);
        }

        // anchors that fell outside every segment are positional diagnostics (incomplete layout)
        foreach (var a in anchors)
        {
            if (!sections.Any(s => a.DepthM >= s.Top - DepthToleranceM && a.DepthM <= s.Base + DepthToleranceM))
                diagnostics.Add(new("ANCHOR_OUTSIDE_SECTION", "error", $"$.anchors[{a.Id}].depth",
                    $"Anchor {a.Id} at {F(a.DepthM)} m is not inside any segment; no null/default position invented.",
                    new[] { a.Id }));
        }
        return sections;
    }

    private void DetectDuplicatesAndInversion(SectionSlot sec,
        List<DeriveDiagnostic> diagnostics, List<Finding> findings)
    {
        // co-located group: identical depths. Equal median is a defensible tie; disagreeing ages are
        // a duplicated-depth conflict and block this section (other sections keep deriving).
        var groups = sec.Anchors.GroupBy(a => Math.Round(a.DepthM / DepthToleranceM) * DepthToleranceM)
                               .Where(g => g.Count() > 1).ToList();
        foreach (var g in groups)
        {
            var ids = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var med = g.Select(x => Median(x.Samples)).ToList();
            var spread = med.Max() - med.Min();
            var sigmaPool = Math.Sqrt(g.Sum(x => Math.Pow(Sd(x.Samples), 2)) / g.Count());
            if (spread <= Math.Max(1.5 * sigmaPool, 120.0))
            {
                findings.Add(new Finding(FindingKind.CoLocatedTie,
                    $"Anchors {string.Join(",", ids)} share depth {F(g.First().DepthM)} m and ages agree; kept as co-equal alternatives.",
                    ids, new Dictionary<string,string> { ["depthM"] = F(g.First().DepthM), ["spreadCalBP"] = F(spread) }));
            }
            else
            {
                sec.Blocked = true;
                sec.BlockReason = $"duplicated depth with incompatible ages ({F(spread)} cal BP apart)";
                diagnostics.Add(new("DEPTH_DUPLICATE", "error", $"$.segments[{sec.SegmentId}]",
                    $"Depth {F(g.First().DepthM)} m carries anchors {string.Join(",", ids)} with incompatible ages; " +
                    "the section cannot be uniquely dated and is blocked (sibling sections unaffected).", ids));
                findings.Add(new Finding(FindingKind.DepthDuplicate,
                    $"Depth duplicated at {F(g.First().DepthM)} m with incompatible ages; section {sec.SegmentId} blocked.",
                    ids.Concat(new[] { sec.SegmentId }).ToList(),
                    new Dictionary<string,string> { ["scope"] = "section:" + sec.SegmentId, ["contained"] = "true" }));
            }
        }

        // within-section order (distinct depths) must be age-monotonic downward (older down-core)
        var distinct = sec.Anchors.GroupBy(a => Math.Round(a.DepthM / DepthToleranceM))
            .Select(g => g.OrderBy(x => Median(x.Samples)).First()).ToList();
        for (var i = 1; i < distinct.Count; i++)
        {
            var upper = distinct[i - 1];
            var lower = distinct[i];
            // require the distributions to credibly separate: lower median < upper median - 1 sigma
            if (Median(lower.Samples) < Median(upper.Samples) - Sd(upper.Samples))
            {
                sec.Blocked = true;
                sec.BlockReason = $"age inversion between {upper.Id} and {lower.Id}";
                diagnostics.Add(new("AGE_INVERSION", "error", $"$.segments[{sec.SegmentId}]",
                    $"Within continuous section {sec.SegmentId}, deeper anchor {lower.Id} " +
                    $"({F(Median(lower.Samples))} cal BP) is younger than {upper.Id} ({F(Median(upper.Samples))} cal BP). " +
                    "Monotonicity is only required inside uninterrupted sections; this violation makes it semantically unsolvable here.",
                    new[] { upper.Id, lower.Id, sec.SegmentId }));
                findings.Add(new Finding(FindingKind.AgeInversion,
                    $"Age inversion in uninterrupted section {sec.SegmentId}: {upper.Id} -> {lower.Id}.",
                    new[] { upper.Id, lower.Id, sec.SegmentId },
                    new Dictionary<string,string> { ["upperCalBP"] = F(Median(upper.Samples)), ["lowerCalBP"] = F(Median(lower.Samples)) }));
            }
        }
    }

    private record VariantDef(IReadOnlyList<string> AnchorIds, IReadOnlyList<string> GroupRepresentatives, string Label);

    // ---------------------------------------------------------------- enumerate defensible variants
    private List<VariantDef> EnumerateVariants(List<SectionSlot> sections,
        List<DeriveDiagnostic> diagnostics, List<Finding> findings, out bool budgetExhausted)
    {
        budgetExhausted = false;
        var unblocked = sections.Where(s => !s.Blocked).ToList();
        if (unblocked.Count == 0) return new();

        // choices per co-located group: each member alone, plus a pooled option (pooled empirical dist)
        var choices = new List<List<Choice>>();
        foreach (var sec in unblocked)
        {
            var groups = sec.Anchors.GroupBy(a => Math.Round(a.DepthM / DepthToleranceM)).ToList();
            foreach (var g in groups)
            {
                if (g.Count() == 1)
                {
                    choices.Add(new() { new Choice(g.Key, g.Select(x => x.Id).ToList(), g.First().Id, "single") });
                }
                else
                {
                    var ids = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var list = ids.Select(id => new Choice(g.Key, ids, id, id)).ToList();
                    list.Add(new Choice(g.Key, ids, string.Join("+", ids), "pooled"));
                    choices.Add(list);
                }
            }
        }

        var combos = Cartesian(choices);
        var variants = new List<VariantDef>();
        var used = 0;
        foreach (var combo in combos)
        {
            if (used >= _p.CandidateBudget)
            {
                budgetExhausted = true;
                findings.Add(new Finding(FindingKind.BudgetExhausted,
                    $"Candidate enumeration budget ({_p.CandidateBudget}) exhausted; remaining combos not explored.",
                    Array.Empty<string>(), new Dictionary<string,string> { ["budget"] = _p.CandidateBudget.ToString() }));
                break;
            }
            var anchorIds = combo.Where(c => c.Kind != "pooled").Select(c => c.Pick).ToList();
            var pooledIds = combo.Where(c => c.Kind == "pooled").SelectMany(c => c.MemberIds).ToList();
            var all = anchorIds.Concat(pooledIds).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var label = string.Join("|", combo.OrderBy(c => c.DepthKey).Select(c => c.Kind == "pooled" ? "pool(" + string.Join("+", c.MemberIds) + ")" : c.Pick));
            variants.Add(new VariantDef(all, combo.Select(c => c.Pick).ToList(), label));
            used++;
        }
        return variants;
    }

    private record Choice(double DepthKey, IReadOnlyList<string> MemberIds, string Pick, string Kind);

    private static IEnumerable<List<Choice>> Cartesian(List<List<Choice>> groups)
    {
        IEnumerable<List<Choice>> acc = new[] { new List<Choice>() };
        foreach (var grp in groups)
            acc = acc.SelectMany(soFar => grp.Select(c => soFar.Concat(new[] { c }).ToList()));
        return acc;
    }

    // ---------------------------------------------------------------- build one candidate model
    private CandidateModel BuildCandidate(VariantDef variant, List<SectionSlot> sections,
        List<EffectiveAnchor> allAnchors, MaterialSet materials, DeterministicRng rng,
        DeriveContext ctx, ref int drawsUsed)
    {
        var idContent = $"cand|{variant.Label}|{string.Join(",", variant.AnchorIds)}";
        var cid = StableId("cand", idContent);

        // selected anchor samples (pooled groups are combined into one empirical distribution)
        var selected = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var pooledDepthKeys = new HashSet<double>();
        foreach (var sec in sections.Where(s => !s.Blocked))
        {
            var groups = sec.Anchors.GroupBy(a => Math.Round(a.DepthM / DepthToleranceM)).ToList();
            foreach (var g in groups)
            {
                var ids = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var pick = variant.GroupRepresentatives.FirstOrDefault(p =>
                    p == string.Join("+", ids));
                if (pick is not null)
                {
                    var pool = g.SelectMany(x => x.Samples).ToArray();
                    var resampled = new double[_p.MonteCarloDraws];
                    for (var i = 0; i < resampled.Length; i++)
                        resampled[i] = pool[rng.NextU64() % (ulong)pool.Length];
                    var key = "pool@" + F(g.First().DepthM);
                    selected[key] = resampled;
                    pooledDepthKeys.Add(g.Key);
                }
                else
                {
                    var rep = variant.GroupRepresentatives.FirstOrDefault(p => ids.Contains(p) && p != string.Join("+", ids));
                    if (rep is not null)
                    {
                        var a = g.First(x => x.Id == rep);
                        selected[a.Id] = a.Samples;
                    }
                }
            }
        }

        var modelSections = new List<ModelSection>();
        var rates = new List<RateInterval>();
        var band = new List<BandPoint>();
        var monotonicEverywhere = true;

        foreach (var sec in sections)
        {
            var secAnchors = OrderedSelected(sec, selected);
            var valid = !sec.Blocked;
            var reason = sec.Blocked ? sec.BlockReason : null;
            if (sec.Blocked) monotonicEverywhere = false;

            modelSections.Add(new ModelSection(
                StableId("sec", $"{cid}|{sec.SegmentId}"),
                sec.Top, sec.Base, sec.TopB, sec.BotB, valid, reason,
                secAnchors.Select(x => x.IdLabel).ToList()));

            if (!valid || secAnchors.Count < 1) continue;

            // enforce monotonicity draw-by-draw inside the uninterrupted section
            var depths = secAnchors.Select(a => a.DepthM).ToArray();
            var sampleMatrix = secAnchors.Select(a => (double[])a.Samples.Clone()).ToArray();
            EnforceMonotone(sampleMatrix);
            drawsUsed += _p.MonteCarloDraws * secAnchors.Count;

            // rates between adjacent anchors only; never across the explicit boundary
            for (var i = 1; i < secAnchors.Count; i++)
            {
                var dd = secAnchors[i].DepthM - secAnchors[i - 1].DepthM;
                var perDraw = new double[_p.MonteCarloDraws];
                for (var k = 0; k < perDraw.Length; k++)
                {
                    var ageDiff = sampleMatrix[i][k] - sampleMatrix[i - 1][k]; // older down-core, positive
                    perDraw[k] = ageDiff > 1e-9 ? dd / (ageDiff / 1000.0) : double.NaN;
                }
                Array.Sort(perDraw);
                var rid = StableId("rate", $"{cid}|{sec.SegmentId}|{i}");
                rates.Add(new RateInterval(rid, modelSections[^1].Id,
                    secAnchors[i - 1].DepthM, secAnchors[i].DepthM,
                    MedianOf(perDraw), QOf(perDraw, .05), QOf(perDraw, .95), true, null));
            }
            // an interval that *would* span the bottom boundary is exposed, invalid, with a reason
            if (secAnchors.Count > 0)
            {
                var last = secAnchors[^1];
                rates.Add(new RateInterval(
                    StableId("rate-xb", $"{cid}|{sec.SegmentId}|{sec.BotB}"),
                    modelSections[^1].Id, last.DepthM, sec.Base,
                    double.NaN, double.NaN, double.NaN, false,
                    sec.BotB == "continuous" ? null : $"refuses to span explicit {sec.BotB} boundary at {F(sec.Base)} m"));
            }

            // uncertainty band on a fixed depth grid, inside [first,last] anchor of this section
            var d0 = depths.First();
            var d1 = depths.Last();
            for (var d = d0; d <= d1 + 1e-9; d += _p.BandGridStepM)
            {
                var dd2 = Math.Min(d, d1);
                var perDraw = new double[_p.MonteCarloDraws];
                for (var k = 0; k < perDraw.Length; k++)
                    perDraw[k] = InterpolateAt(depths, sampleMatrix, dd2, k);
                Array.Sort(perDraw);
                band.Add(new BandPoint(
                    StableId("band", $"{cid}|{sec.SegmentId}|{F(dd2)}"),
                    modelSections[^1].Id, dd2, MedianOf(perDraw), QOf(perDraw, .05), QOf(perDraw, .95)));
            }
        }

        var score = LeaveOneOutScore(sections, selected);
        return new CandidateModel
        {
            Id = cid,
            VariantLabel = variant.Label,
            ChosenAnchorIds = variant.AnchorIds,
            ScoreSigma = score,
            Sections = modelSections,
            Rates = rates,
            Band = band
        };
    }

    private record SelectedPoint(string IdLabel, double DepthM, double[] Samples);

    private static List<SelectedPoint> OrderedSelected(SectionSlot sec, Dictionary<string, double[]> selected)
    {
        var pts = new List<SelectedPoint>();
        foreach (var a in sec.Anchors)
        {
            if (selected.TryGetValue(a.Id, out var s)) pts.Add(new SelectedPoint(a.Id, a.DepthM, s));
        }
        foreach (var kv in selected)
        {
            if (kv.Key.StartsWith("pool@", StringComparison.Ordinal))
            {
                var d = double.Parse(kv.Key["pool@".Length..], Culture.Inv);
                if (d >= sec.Top - DepthToleranceM && d <= sec.Base + DepthToleranceM)
                    pts.Add(new SelectedPoint(kv.Key, d, kv.Value));
            }
        }
        return pts.OrderBy(p => p.DepthM).ThenBy(p => p.IdLabel, StringComparer.Ordinal).ToList();
    }

    /// <summary>Isotonic projection: for every Monte Carlo draw, ages must not get younger down-core.</summary>
    private static void EnforceMonotone(double[][] columns)
    {
        var n = columns[0].Length;
        for (var k = 0; k < n; k++)
        {
            for (var i = 1; i < columns.Length; i++)
                if (columns[i][k] < columns[i - 1][k]) columns[i][k] = columns[i - 1][k];
        }
    }

    private static double InterpolateAt(double[] depths, double[][] cols, double depth, int k)
    {
        if (depth <= depths[0]) return cols[0][k];
        if (depth >= depths[^1]) return cols[^1][k];
        for (var i = 1; i < depths.Length; i++)
        {
            if (depth <= depths[i])
            {
                var t = (depth - depths[i - 1]) / (depths[i] - depths[i - 1]);
                return cols[i - 1][k] + t * (cols[i][k] - cols[i - 1][k]);
            }
        }
        return cols[^1][k];
    }

    private double LeaveOneOutScore(List<SectionSlot> sections, Dictionary<string, double[]> selected)
    {
        var residuals = new List<double>();
        foreach (var sec in sections.Where(s => !s.Blocked))
        {
            var pts = OrderedSelected(sec, selected);
            if (pts.Count < 3) { foreach (var p in pts) residuals.Add(0.0); continue; }
            for (var leave = 0; leave < pts.Count; leave++)
            {
                var kept = pts.Where((_, i) => i != leave).OrderBy(p => p.DepthM).ToList();
                var target = pts[leave];
                var kd = new double[kept.Count][];
                for (var i = 0; i < kept.Count; i++) { kd[i] = (double[])kept[i].Samples.Clone(); }
                EnforceMonotone(kd);
                var depths = kept.Select(p => p.DepthM).ToArray();
                var med = MedianOf(target.Samples);
                var sd = Math.Max(Sd(target.Samples), 1.0);
                var preds = new double[target.Samples.Length];
                for (var k = 0; k < preds.Length; k++)
                {
                    var col = kd.Select(c => c[k]).ToArray();
                    preds[k] = InterpolateAt(depths, kd, target.DepthM, k);
                }
                residuals.Add(Math.Abs(MedianOf(preds) - med) / sd);
            }
        }
        residuals.Sort();
        return residuals.Count == 0 ? double.PositiveInfinity : MedianOf(residuals.ToArray());
    }

    // ---------------------------------------------------------------- rank + retain ties
    private List<CandidateModel> RankCandidates(List<CandidateModel> candidates, List<Finding> findings)
    {
        var ranked = candidates.OrderBy(c => c.ScoreSigma)
                               .ThenBy(c => c.VariantLabel, StringComparer.Ordinal).ToList();
        if (ranked.Count == 0) return ranked;
        var best = ranked[0].ScoreSigma;
        var tieCount = 0;
        for (var i = 0; i < ranked.Count; i++)
        {
            var tie = Math.Abs(ranked[i].ScoreSigma - best) <= _p.TieToleranceSigma;
            if (tie) tieCount++;
            ranked[i] = ranked[i] with { EquallyOptimal = tie };
        }
        if (tieCount > 1)
            findings.Add(new Finding(FindingKind.MultipleOptimaRetained,
                $"{tieCount} candidates are equally optimal within {_p.TieToleranceSigma} sigma; all retained, none silently chosen.",
                ranked.Take(tieCount).Select(c => c.Id).ToList(),
                new Dictionary<string,string> { ["bestScoreSigma"] = F(best), ["tieToleranceSigma"] = F(_p.TieToleranceSigma) }));
        findings.Add(new Finding(FindingKind.DistributionKeptNotMean,
            "Each anchor entered the model as a sampled distribution; medians are display summaries only.",
            ranked.SelectMany(c => c.ChosenAnchorIds).Distinct().ToList(),
            new Dictionary<string,string> { ["draws"] = _p.MonteCarloDraws.ToString() }));
        findings.Add(new Finding(FindingKind.MonotonicWithinSection,
            "Monotonicity enforced draw-by-draw inside uninterrupted sections; gaps/unconformities are boundaries.",
            ranked.SelectMany(c => c.Sections.Select(s => s.Id)).Distinct().ToList(),
            new Dictionary<string,string>()));
        return ranked;
    }

    // ---------------------------------------------------------------- digest + helpers
    public string ComputeDigest(MaterialSet m, DeriveContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("rev=").Append(m.RevisionId);
        sb.Append(";datum=").Append(m.Datum.Id).Append('@').Append(F(m.Datum.ZeroMbsf));
        foreach (var s in m.Segments.OrderBy(x => x.Id))
            sb.Append(";seg:").Append(s.Id).Append('=').Append(F(s.TopDepthM)).Append('-').Append(F(s.BaseDepthM))
              .Append('/').Append(s.BaseRelation);
        foreach (var a in m.Anchors.OrderBy(x => x.Id))
        {
            sb.Append(";a:").Append(a.Id).Append('@').Append(F(a.Depth.Normalized ?? double.NaN))
              .Append('#').Append(a.Distribution.DistributionKind)
              .Append(':').Append(a.Distribution.SamplesCalBP.Length)
              .Append(',').Append(F(a.Distribution.MuCalBP ?? double.NaN))
              .Append(',').Append(F(a.Distribution.SigmaCalBP ?? double.NaN))
              .Append(',').Append(F(a.Distribution.Skew ?? 0))
              .Append(a.Excluded ? "X" : "").Append("ro=").Append(a.ReservoirOffsetId ?? "");
        }
        foreach (var r in m.ReservoirOffsets.OrderBy(x => x.Id))
            sb.Append(";ro:").Append(r.Id).Append('=').Append(F(r.DeltaR.Normalized ?? double.NaN))
              .Append(',').Append(F(r.SigmaR.Normalized ?? double.NaN));
        sb.Append(";rules=").Append(_p.VersionId).Append('|').Append(_p.MonteCarloDraws)
          .Append('|').Append(F(_p.BandGridStepM)).Append('|').Append(F(_p.TieToleranceSigma))
          .Append('|').Append(_p.CandidateBudget);
        sb.Append(";parents=").Append(string.Join(",", ctx.Parents.OrderBy(x => x, StringComparer.Ordinal)));
        return Hashing.Sha256Hex(sb.ToString());
    }

    private static string F(double v) =>
        double.IsNaN(v) ? "NaN" : v.ToString("0.######", Culture.Inv);
    private static double Median(double[] x) => MedianOf(x);
    private static double MedianOf(double[] x)
    {
        if (x.Length == 0) return double.NaN;
        var y = x.Where(v => !double.IsNaN(v)).ToArray();
        if (y.Length == 0) return double.NaN;
        Array.Sort(y);
        return y.Length % 2 == 1 ? y[y.Length / 2] : (y[y.Length / 2 - 1] + y[y.Length / 2]) / 2;
    }
    private static double Q(double[] x, double p) => QOf(x, p);
    private static double QOf(double[] x, double p)
    {
        var y = x.Where(v => !double.IsNaN(v)).ToArray();
        if (y.Length == 0) return double.NaN;
        Array.Sort(y);
        return y[Math.Clamp((int)Math.Round(p * (y.Length - 1)), 0, y.Length - 1)];
    }
    private static double Sd(double[] x)
    {
        if (x.Length < 2) return 0;
        var mean = x.Average();
        return Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1));
    }
    private static class Culture
    {
        public static readonly IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;
    }
}

/// <summary>Where a derivation sits in the recompute graph.</summary>
public sealed record DeriveContext(string MaterialRevisionId, IReadOnlyList<string> Parents);
