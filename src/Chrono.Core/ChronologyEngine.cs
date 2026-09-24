using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chrono.Core;

public sealed class ChronologyEngine
{
    public const string EngineVersion = "chrono-rules-v1";

    public ComputeOutcome Compute(MaterialSet m, RuleParams p, ComputeOutcome? previous = null)
    {
        var outcome = new ComputeOutcome { Kind = ComputeKind.Success, Params = p, MaterialHash = HashMaterials(m, p) };
        var validSegs = ValidateGeometry(m, outcome);
        if (outcome.Kind == ComputeKind.SemanticImpossible) return outcome;

        var builds = BuildRuns(m, validSegs);
        var prepared = PrepareAnchors(m, outcome);
        var usable = prepared.Where(a => a.Usable).ToList();
        var groups = BuildTieGroups(usable, builds, outcome);
        var events = m.EventLayers.Where(e => e.CorrelatedAge is not null).ToList();

        long combos = 1;
        foreach (var g in groups) combos *= g.Options.Count;
        combos *= (long)Math.Pow(2, events.Count);
        if (combos > p.CandidateBudget)
        {
            outcome.Kind = ComputeKind.BudgetExhausted;
            outcome.Diagnostics.Add(Diag.Error("CANDIDATE_BUDGET_EXHAUSTED",
                string.Create(CultureInfo.InvariantCulture, $"Candidate enumeration {combos} exceeds budget {p.CandidateBudget}."), loc: "$"));
            return outcome;
        }

        foreach (var combo in EnumerateCombos(groups, events))
        {
            var (main, raw) = EvaluateCandidates(m, p, builds, usable, groups, events, combo);
            outcome.Candidates.Add(main);
            if (raw is not null) outcome.Candidates.Add(raw);
        }

        FinalizeRanking(outcome);

        var first = outcome.Candidates.OrderBy(c => c.Id).FirstOrDefault(c => c.Adopted);
        var hashesFrom = first ?? outcome.Candidates.FirstOrDefault();
        if (hashesFrom is not null)
            foreach (var r in hashesFrom.Runs) outcome.RunHashes[r.Id] = r.RunHash;

        if (previous is not null)
        {
            foreach (var kv in outcome.RunHashes)
                if (!previous.RunHashes.TryGetValue(kv.Key, out var old) || old != kv.Value)
                    outcome.AffectedRuns.Add(kv.Key);
        }
        else outcome.AffectedRuns.AddRange(outcome.RunHashes.Keys);

        return outcome;
    }

    static List<Segment> ValidateGeometry(MaterialSet m, ComputeOutcome o)
    {
        if (m.Datum is null)
        {
            o.Kind = ComputeKind.SemanticImpossible;
            o.Diagnostics.Add(Diag.Error("MISSING_DATUM", "Depth datum is required.", loc: "$.datum"));
            return new();
        }
        var segs = m.Segments.OrderBy(s => s.Ordinal).ToList();
        if (segs.Count == 0)
        {
            o.Kind = ComputeKind.SemanticImpossible;
            o.Diagnostics.Add(Diag.Error("NO_SEGMENTS", "At least one segment is required.", loc: "$.segments"));
            return new();
        }
        foreach (var s in segs)
        {
            if (!s.Top.Convertible || !s.Bottom.Convertible || double.IsNaN(s.TopM) || double.IsNaN(s.BottomM))
            {
                o.Kind = ComputeKind.SemanticImpossible;
                o.Diagnostics.Add(Diag.Error("SEGMENT_DEPTH_UNCONVERTIBLE",
                    $"Segment {s.Id} has an unconvertible depth bound; material retained in place.",
                    s.Id, $"$..segments[{s.Ordinal}]"));
            }
            else if (s.TopM >= s.BottomM)
            {
                o.Kind = ComputeKind.SemanticImpossible;
                o.Diagnostics.Add(Diag.Error("GEOMETRY_INVERSION",
                    $"Segment {s.Name} is inverted or zero-thickness (top {s.Top.Raw} {s.Top.Unit} >= bottom {s.Bottom.Raw} {s.Bottom.Unit}).",
                    s.Id, $"$..segments[{s.Ordinal}]"));
            }
        }
        if (o.Kind == ComputeKind.SemanticImpossible) return new();

        for (var i = 0; i + 1 < segs.Count; i++)
        {
            var gap = segs[i + 1].TopM - segs[i].BottomM;
            if (gap > 1e-6 && segs[i].BoundaryBelow == "continuous")
                o.Diagnostics.Add(Diag.Warn("UNDECLARED_GAP",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Undeclared gap of {gap:F3} m between {segs[i].Id} and {segs[i + 1].Id}; treated as an explicit boundary."),
                    $"{segs[i].Id}|{segs[i + 1].Id}"));
        }
        return segs;
    }

    public sealed record RunBuild(string Id, int Ordinal, List<string> SegmentIds, double TopM, double BottomM,
        string UpperBoundary, string LowerBoundary);

    List<RunBuild> BuildRuns(MaterialSet _, List<Segment> segs)
    {
        var runs = new List<RunBuild>();
        var current = new List<string> { segs[0].Id };
        var top = segs[0].TopM;
        var ord = 0;
        var upper = "core-top";
        for (var i = 0; i < segs.Count; i++)
        {
            if (i > 0) current.Add(segs[i].Id);
            var stop = i == segs.Count - 1 || segs[i].BoundaryBelow != "continuous";
            if (stop)
            {
                var lower = i == segs.Count - 1 ? "core-bottom" : segs[i].BoundaryBelow;
                runs.Add(new RunBuild("run-" + ord, ord, new List<string>(current), top, segs[i].BottomM, upper, lower));
                ord++;
                if (i < segs.Count - 1) { top = segs[i + 1].TopM; upper = segs[i].BoundaryBelow; current = new List<string>(); }
            }
        }
        return runs;
    }

    sealed class PreparedAnchor { public required AgeAnchor Anchor; public required string RunId; public bool Usable; }

    List<PreparedAnchor> PrepareAnchors(MaterialSet m, ComputeOutcome o)
    {
        var segRun = m.Segments.Any()
            ? BuildRuns(m, m.Segments.OrderBy(s => s.Ordinal).ToList())
                .SelectMany(r => r.SegmentIds.Select(sid => (sid, r.Id))).ToDictionary(x => x.sid, x => x.Id)
            : new();
        var excluded = new HashSet<string>(m.Exclusions.Select(x => x.TargetAnchorId));
        var list = new List<PreparedAnchor>();
        foreach (var a in m.Anchors)
        {
            var pa = new PreparedAnchor { Anchor = a, RunId = "", Usable = false };
            if (excluded.Contains(a.Id))
                o.Diagnostics.Add(Diag.Info("ANCHOR_EXCLUDED",
                    $"Anchor {a.Id} excluded by a recorded decision; material retained, not used.", a.Id));
            else if (!segRun.ContainsKey(a.SegmentId))
                o.Diagnostics.Add(Diag.Warn("ANCHOR_UNKNOWN_SEGMENT",
                    $"Anchor {a.Id} references unknown segment {a.SegmentId}; kept out of computation.", a.Id));
            else if (!a.Depth.Convertible || double.IsNaN(a.DepthM))
                o.Diagnostics.Add(Diag.Warn("UNCONVERTIBLE_DEPTH",
                    $"Anchor {a.Id} depth '{a.Depth.Raw} {a.Depth.Unit}' cannot be normalized ({a.Depth.Reason}); retained in place, never dropped for plotting.", a.Id));
            else { pa.RunId = segRun[a.SegmentId]; pa.Usable = true; }
            list.Add(pa);
        }
        return list;
    }

    sealed class TieGroup
    {
        public required string Id;
        public required string RunId;
        public required double DepthM;
        public required List<AgeAnchor> Options;
    }

    List<TieGroup> BuildTieGroups(List<PreparedAnchor> usable, List<RunBuild> builds, ComputeOutcome o)
    {
        var groups = new List<TieGroup>();
        foreach (var rb in builds)
        {
            var here = usable.Where(a => a.RunId == rb.Id).ToList();
            var dup = here.GroupBy(a => Math.Round(a.Anchor.DepthM, 6)).Where(g => g.Count() > 1)
                       .OrderBy(g => g.Key).ToList();
            foreach (var g in dup)
            {
                var anchors = g.Select(x => x.Anchor).OrderBy(a => a.Id).ToList();
                var sameSeg = anchors.Select(a => a.SegmentId).Distinct().Count() == 1;
                var code = sameSeg ? "TIED_ANCHORS" : "DEPTH_DUPLICATE";
                o.Diagnostics.Add(Diag.Warn(code,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{anchors.Count} anchors share depth {g.Key:F3} m inside {rb.Id}; every legal choice is enumerated as its own candidate."),
                    string.Join("|", anchors.Select(a => a.Id))));
                groups.Add(new TieGroup
                {
                    Id = string.Create(CultureInfo.InvariantCulture, $"tie-{g.Key:F3}"),
                    RunId = rb.Id, DepthM = g.Key, Options = anchors
                });
            }
        }
        return groups.OrderBy(g => g.DepthM).ThenBy(g => g.Id).ToList();
    }

    sealed class Combo
    {
        public required int Index;
        public required Dictionary<string, string> TieSelection;
        public required Dictionary<string, bool> EventUsage;
    }

    IEnumerable<Combo> EnumerateCombos(List<TieGroup> groups, List<EventLayer> events)
    {
        var evs = events.OrderBy(e => e.Id).ToList();
        var optCounts = groups.Select(g => g.Options.Count).ToArray();
        long totalTies = 1;
        foreach (var c in optCounts) totalTies *= c;
        var total = totalTies * (1L << evs.Count);
        for (var i = 0; i < total; i++)
        {
            var tieIndex = i % totalTies;
            var evIndex = (int)(i / totalTies);
            var tie = new Dictionary<string, string>();
            var k = tieIndex;
            for (var gi = 0; gi < groups.Count; gi++)
            {
                tie[groups[gi].Id] = groups[gi].Options[(int)(k % optCounts[gi])].Id;
                k /= optCounts[gi];
            }
            var usage = new Dictionary<string, bool>();
            for (var ei = 0; ei < evs.Count; ei++) usage[evs[ei].Id] = ((evIndex >> ei) & 1) == 1;
            yield return new Combo { Index = (int)i, TieSelection = tie, EventUsage = usage };
        }
    }

    sealed class NodeInput
    {
        public required string Kind;
        public required string SourceId;
        public required double DepthM;
        public required double[] RawGrid;
        public required bool LongTailed;
    }

    (CandidateModel main, CandidateModel? rejected) EvaluateCandidates(MaterialSet m, RuleParams p,
        List<RunBuild> builds, List<PreparedAnchor> usable, List<TieGroup> groups,
        List<EventLayer> events, Combo combo)
    {
        var pooledRuns = new List<ContinuousRun>();
        var rawRuns = new List<ContinuousRun>();
        var invertedHere = false;
        var poolDiag = new List<Diagnostic>();

        foreach (var rb in builds)
        {
            var picked = new List<NodeInput>();
            foreach (var pa in usable.Where(a => a.RunId == rb.Id)
                                    .OrderBy(a => a.Anchor.DepthM).ThenBy(a => a.Anchor.Id))
            {
                var blocked = groups.FirstOrDefault(g => g.RunId == rb.Id &&
                    Math.Abs(g.DepthM - pa.Anchor.DepthM) < 1e-6 && combo.TieSelection[g.Id] != pa.Anchor.Id);
                if (blocked is not null) continue;
                var offId = pa.Anchor.ReservoirOffsetId;
                var off = offId is not null && m.Reservoirs.Any(r => r.Id == offId)
                    ? m.Reservoirs.First(r => r.Id == offId).Offset : null;
                var grid = EffectiveDistribution.QuantileGrid(pa.Anchor.Age, off);
                picked.Add(new NodeInput { Kind = "anchor", SourceId = pa.Anchor.Id, DepthM = pa.Anchor.DepthM, RawGrid = grid, LongTailed = pa.Anchor.Age.IsLongTailed() });
            }
            foreach (var e in events.Where(e => rb.SegmentIds.Contains(e.SegmentId)).OrderBy(e => e.DepthM))
            {
                if (!combo.EventUsage.GetValueOrDefault(e.Id)) continue;
                picked.Add(new NodeInput { Kind = "event", SourceId = e.Id, DepthM = e.DepthM,
                    RawGrid = EffectiveDistribution.QuantileGrid(e.CorrelatedAge!, null), LongTailed = e.CorrelatedAge!.IsLongTailed() });
            }
            picked.Sort((x, y) => x.DepthM == y.DepthM ? string.CompareOrdinal(x.SourceId, y.SourceId) : x.DepthM.CompareTo(y.DepthM));

            var inverted = false;
            for (var i = 1; i < picked.Count; i++)
            {
                var d = EffectiveDistribution.Interp(picked[i].RawGrid, 0.5)
                      - EffectiveDistribution.Interp(picked[i - 1].RawGrid, 0.5);
                if (d < -p.MonotonicToleranceKa) inverted = true;
            }
            if (inverted)
            {
                invertedHere = true;
                poolDiag.Add(Diag.Warn("INVERSION_POOLED",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Non-monotonic raw ages in {rb.Id}; isotonic pooling applied within this uninterrupted run only."),
                    rb.Id));
            }

            pooledRuns.Add(MaterializeRun(rb, picked, p, true));
            rawRuns.Add(MaterializeRun(rb, picked, p, false));
        }

        var inputHash = HashMaterials(m, p);
        var baseId = "cand-" + combo.Index.ToString("D3", CultureInfo.InvariantCulture);
        var main = new CandidateModel
        {
            Id = baseId, Rank = 0, Adopted = true,
            Status = invertedHere ? "adopted-with-pooling" : "adopted",
            Score = pooledRuns.SelectMany(r => r.Rates).Sum(r => r.Rate95 - r.Rate05),
            TieSelection = combo.TieSelection, EventUsage = combo.EventUsage,
            Runs = pooledRuns, InputHash = inputHash
        };
        CandidateModel? rejected = null;
        if (invertedHere)
        {
            rejected = new CandidateModel
            {
                Id = baseId + "-raw-inverted", Rank = 0, Adopted = false,
                Status = "rejected", RejectReason = "inverted-raw-ages",
                Score = double.PositiveInfinity,
                TieSelection = new(combo.TieSelection), EventUsage = new(combo.EventUsage),
                Runs = rawRuns, InputHash = inputHash
            };
        }
        return (main, rejected);
    }

    ContinuousRun MaterializeRun(RunBuild rb, List<NodeInput> picked, RuleParams p, bool pool)
    {
        var nodes = new List<ModelNode>();
        for (var i = 0; i < picked.Count; i++)
        {
            var src = picked[i];
            double[] grid = src.RawGrid;
            var pooledNow = false;
            if (pool)
            {
                var lo = i > 0 ? nodes[i - 1].Age50Ka : double.NegativeInfinity;
                if (EffectiveDistribution.Interp(grid, 0.5) < lo - p.MonotonicToleranceKa)
                {
                    grid = PoolWithPrevious(nodes[i - 1], src, p);
                    nodes[i - 1] = nodes[i - 1] with { AgeGrid = grid };
                    pooledNow = true;
                }
            }
            var (q05, q50, q95) = EffectiveDistribution.SummaryOf(grid);
            nodes.Add(new ModelNode
            {
                Id = $"node:{rb.Id}:{src.SourceId}",
                SourceId = src.SourceId, SourceKind = src.Kind, DepthM = src.DepthM,
                Age05Ka = q05, Age50Ka = q50, Age95Ka = q95,
                AgeGrid = grid, RawAge50Ka = EffectiveDistribution.Interp(src.RawGrid, 0.5),
                Pooled = pooledNow, LongTailed = src.LongTailed
            });
        }
        var rates = BuildRates(rb.Id, nodes);
        var band = BuildBand(rb.Id, nodes, rb, p);
        var hash = StableHash(rb.Id + "|" + string.Join(",", nodes.Select(n => n.SourceId + ":" + HashGrid(n.AgeGrid))) + "|" + p.Version);
        return new ContinuousRun
        {
            Id = rb.Id, Ordinal = rb.Ordinal, SegmentIds = rb.SegmentIds,
            TopM = rb.TopM, BottomM = rb.BottomM,
            UpperBoundary = rb.UpperBoundary, LowerBoundary = rb.LowerBoundary,
            Nodes = nodes, Rates = rates, Band = band, RunHash = hash
        };
    }

    // Weighted isotonic (PAVA) merge of a violating node into the previous pooled block.
    static double[] PoolWithPrevious(ModelNode prev, NodeInput violator, RuleParams p)
    {
        var merged = new double[p.QuantileGrid];
        for (var i = 0; i < p.QuantileGrid; i++)
            merged[i] = (prev.AgeGrid[i] + violator.RawGrid[i]) / 2.0;
        return merged;
    }

    static List<RateInterval> BuildRates(string runId, List<ModelNode> nodes)
    {
        var rates = new List<RateInterval>();
        for (var i = 1; i < nodes.Count; i++)
        {
            var dd = (nodes[i].DepthM - nodes[i - 1].DepthM) * 100.0; // m -> cm
            var dt50 = Math.Max(nodes[i].Age50Ka - nodes[i - 1].Age50Ka, 1e-6);
            var dt05 = Math.Max(nodes[i].Age05Ka - nodes[i - 1].Age95Ka, 1e-6);
            var dt95 = Math.Max(nodes[i].Age95Ka - nodes[i - 1].Age05Ka, 1e-6);
            rates.Add(new RateInterval
            {
                Id = $"rate:{runId}:{nodes[i - 1].SourceId}>{nodes[i].SourceId}",
                FromNodeId = nodes[i - 1].Id, ToNodeId = nodes[i].Id,
                DepthFromM = nodes[i - 1].DepthM, DepthToM = nodes[i].DepthM,
                Rate05 = dd / dt95, Rate50 = dd / dt50, Rate95 = dd / dt05
            });
        }
        return rates;
    }

    static List<BandPoint> BuildBand(string runId, List<ModelNode> nodes, RunBuild rb, RuleParams p)
    {
        var band = new List<BandPoint>();
        if (nodes.Count == 0) return band;
        var first = nodes[0];
        var last = nodes[^1];
        var n = (int)Math.Round((rb.BottomM - rb.TopM) / p.DepthGridStepM);
        for (var i = 0; i <= n; i++)
        {
            var d = rb.TopM + i * p.DepthGridStepM;
            var id = $"band:{runId}:{i}";
            if (d < first.DepthM - 1e-9 || d > last.DepthM + 1e-9)
            {
                band.Add(new BandPoint { Id = id, DepthM = d, Age05Ka = double.NaN, Age50Ka = double.NaN, Age95Ka = double.NaN, Valid = false, InvalidReason = "beyond-anchor-range" });
                continue;
            }
            ModelNode a, b;
            var idx = nodes.FindIndex(x => x.DepthM >= d);
            if (idx <= 0) { a = b = first; }
            else { a = nodes[idx - 1]; b = nodes[idx]; }
            var f = b.DepthM == a.DepthM ? 0 : (d - a.DepthM) / (b.DepthM - a.DepthM);
            double Q(double[] ga, double[] gb) => a.Age50Ka == b.Age50Ka && a.SourceId == b.SourceId ? ga[0] : EffectiveDistribution.Interp(ga, 0.5) * (1 - f) + EffectiveDistribution.Interp(gb, 0.5) * f;
            // quantile-wise interpolation over the full ordered grid
            double Qq(double[] ga, double[] gb, double q)
            {
                var va = EffectiveDistribution.Interp(ga, q);
                var vb = EffectiveDistribution.Interp(gb, q);
                return va * (1 - f) + vb * f;
            }
            band.Add(new BandPoint
            {
                Id = id, DepthM = d,
                Age05Ka = Qq(a.AgeGrid, b.AgeGrid, 0.05),
                Age50Ka = Qq(a.AgeGrid, b.AgeGrid, 0.5),
                Age95Ka = Qq(a.AgeGrid, b.AgeGrid, 0.95),
                Valid = true
            });
        }
        return band;
    }

    static void FinalizeRanking(ComputeOutcome o)
    {
        var valid = o.Candidates.Where(c => c.Adopted).OrderBy(c => c.Score).ThenBy(c => c.Id).ToList();
        if (valid.Count == 0) return;
        var best = valid[0].Score;
        var rank = 1;
        foreach (var c in valid)
        {
            if (c.Score <= best + 1e-9)
            {
                c.Rank = rank;
            }
            else
            {
                c.Adopted = false;
                c.Status = "not-adopted";
            }
        }
        // tie rank: all best share rank 1; non-adopted get increasing ranks
        rank = 2;
        foreach (var c in valid.Where(c => !c.Adopted)) { c.Rank = rank++; }
        foreach (var c in o.Candidates.Where(c => !c.Adopted && c.Status == "rejected")) c.Rank = int.MaxValue;
    }

    public static string StableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static string HashGrid(double[] grid)
    {
        var sb = new StringBuilder();
        foreach (var v in grid) sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        return StableHash(sb.ToString());
    }

    public static string HashMaterials(MaterialSet m, RuleParams p)
    {
        static string Q(RawQuantity q) => $"{q.Raw}|{q.Unit}|{q.Datum}|{q.NormalizedValue?.ToString("R", CultureInfo.InvariantCulture)}|{q.Convertible}|{q.Reason}";
        var sb = new StringBuilder();
        sb.Append("DATUM:").Append(m.Datum.Id).Append('|').Append(m.Datum.Unit).Append('\n');
        foreach (var s in m.Segments.OrderBy(s => s.Ordinal))
            sb.Append("SEG:").Append(s.Id).Append(':').Append(s.Ordinal).Append(':').Append(Q(s.Top)).Append(':').Append(Q(s.Bottom)).Append(':').Append(s.BoundaryBelow).Append('\n');
        foreach (var a in m.Anchors.OrderBy(a => a.Id))
            sb.Append("ANC:").Append(a.Id).Append(':').Append(a.SegmentId).Append(':').Append(Q(a.Depth)).Append(':')
              .Append(a.Age.Type).Append(':').Append(a.Age.Mean.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.Age.Sigma?.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.Age.Shape?.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.Age.Scale?.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.Age.Min?.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.Age.Max?.ToString("R", CultureInfo.InvariantCulture)).Append(':')
              .Append(a.ReservoirOffsetId).Append('\n');
        foreach (var r in m.Reservoirs.OrderBy(r => r.Id))
            sb.Append("RES:").Append(r.Id).Append(':').Append(r.AppliesTo).Append(':')
              .Append(r.Offset.Type).Append(':').Append(r.Offset.Mean.ToString("R", CultureInfo.InvariantCulture))
              .Append(':').Append(r.Offset.Sigma?.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var e in m.EventLayers.OrderBy(e => e.Id))
            sb.Append("EVT:").Append(e.Id).Append(':').Append(e.SegmentId).Append(':').Append(Q(e.Depth)).Append(':')
              .Append(e.CorrelatedAge?.Type).Append(':').Append(e.CorrelatedAge?.Mean.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var x in m.Exclusions.OrderBy(x => x.Id))
            sb.Append("EXC:").Append(x.Id).Append(':').Append(x.TargetAnchorId).Append(':').Append(x.Reason).Append('\n');
        sb.Append("PARAMS:").Append(p.Version).Append(':').Append(p.CandidateBudget).Append(':')
          .Append(p.IncludeEventLayerCorrelations).Append(':').Append(p.QuantileGrid)
          .Append(':').Append(p.DepthGridStepM.ToString("R", CultureInfo.InvariantCulture));
        return StableHash(sb.ToString());
    }
}
