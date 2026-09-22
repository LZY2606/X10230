using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeModel.Core;

public sealed record ModelInput(
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Decisions,
    string RuleVersion = "rules-v1",
    int CandidateBudget = 64);

/// <summary>
/// The depth-age model engine. All business rules live here, callable directly
/// from tests (no browser involvement). Pure: same inputs -> same ordered outputs.
/// </summary>
public sealed class Engine
{
    private const double DepthEps = 1e-6;

    public string Actor { get; init; } = "engine";

    public BuildResult Build(ModelInput input, string batchId, long seqStart = 0,
        HashSet<string>? changedEntityIds = null)
    {
        var diags = new List<Diagnostic>();
        var evts = new List<DomainEvent>();
        long seq = seqStart;
        DomainEvent E(string kind, object payload) => NewEvent(kind, payload, batchId, ref seq);

        // --- structural validation (format corruption is its own outcome) ---
        var dupId = input.Sections.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1)?.Key
                    ?? input.Anchors.GroupBy(a => a.Id).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (dupId != null)
        {
            var d = new Diagnostic("E-FORMAT-DUP", $"实体 id 重复: {dupId}", "entities", DiagSeverity.Error);
            evts.Add(E(Ev.FormatCorrupt, new { diagnostic = d, changedEntityIds }));
            return BuildResult.Failed(BuildStatus.FormatCorrupt, new[] { d }, evts);
        }

        // --- derived layer: normalize quantities; failures stay in place ---
        var normSections = new List<CoreSection>();
        foreach (var s in input.Sections)
        {
            var (top, d1) = UnitRegistry.Normalize(s.Top, "depth", $"{s.Id}.top");
            var (bas, d2) = UnitRegistry.Normalize(s.Base, "depth", $"{s.Id}.base");
            if (d1 != null) { diags.Add(d1); evts.Add(E(Ev.NormalizationFailed, d1)); }
            if (d2 != null) { diags.Add(d2); evts.Add(E(Ev.NormalizationFailed, d2)); }
            if (top.Normalized.HasValue && bas.Normalized.HasValue && bas.Normalized.Value - top.Normalized.Value < -DepthEps)
            {
                var d = new Diagnostic("E-FORMAT-GEOM",
                    $"分段 {s.Id} 底界 ({bas.RawValue}{bas.RawUnit}) 浅于顶界 ({top.RawValue}{top.RawUnit})", $"{s.Id}", DiagSeverity.Error);
                diags.Add(d);
                evts.Add(E(Ev.FormatCorrupt, d));
                return BuildResult.Failed(BuildStatus.FormatCorrupt, new[] { d }, evts);
            }
            normSections.Add(s with { Top = top, Base = bas });
        }

        var normAnchors = new List<AgeAnchor>();
        foreach (var a in input.Anchors)
        {
            var (dep, dd) = UnitRegistry.Normalize(a.Depth, "depth", $"{a.Id}.depth");
            if (dd != null) { diags.Add(dd); evts.Add(E(Ev.NormalizationFailed, dd)); }
            var res = a.ReservoirOffset;
            if (res != null)
            {
                var (r2, dr) = UnitRegistry.Normalize(res, "age", $"{a.Id}.reservoirOffset");
                if (dr != null) { diags.Add(dr); evts.Add(E(Ev.NormalizationFailed, dr)); }
                res = r2;
            }
            normAnchors.Add(a with { Depth = dep, ReservoirOffset = res });
        }

        var normEvents = new List<EventLayer>();
        foreach (var e in input.EventLayers)
        {
            var (dep, dd) = UnitRegistry.Normalize(e.Depth, "depth", $"{e.Id}.depth");
            if (dd != null) { diags.Add(dd); evts.Add(E(Ev.NormalizationFailed, dd)); }
            normEvents.Add(e with { Depth = dep });
        }
        _ = normEvents;

        // --- segmentation: gaps/unconformities are explicit boundaries ---
        var ordered = normSections.Where(x => x.Top.Normalized.HasValue && x.Base.Normalized.HasValue)
                                  .OrderBy(x => x.Top.Normalized!.Value).ToList();
        var segments = new List<Segment>();
        var run = new List<CoreSection>();
        var boundaryAbove = BoundaryKind.None;
        void CloseRun(BoundaryKind below)
        {
            if (run.Count == 0) return;
            segments.Add(new Segment
            {
                Id = "seg-" + segments.Count,
                SectionIds = run.Select(x => x.Id).ToList(),
                TopM = run[0].Top.Normalized!.Value,
                BaseM = run[^1].Base.Normalized!.Value,
                DepthDatum = run[0].Top.Datum,
                BoundaryAbove = boundaryAbove,
                BoundaryBelow = below,
            });
            run.Clear();
        }
        foreach (var s in ordered)
        {
            if (run.Count > 0 && s.Top.Normalized!.Value - run[^1].Base.Normalized!.Value > DepthEps)
            {
                evts.Add(E(Ev.BoundaryDeclared, new
                {
                    kind = nameof(BoundaryKind.Gap),
                    atDepthM = run[^1].Base.Normalized!.Value,
                    between = new[] { run[^1].Id, s.Id },
                    datum = s.Top.Datum,
                }));
                CloseRun(BoundaryKind.Gap);
                boundaryAbove = BoundaryKind.Gap;
            }
            run.Add(s);
            if (s.BoundaryBelow != BoundaryKind.None)
            {
                evts.Add(E(Ev.BoundaryDeclared, new
                {
                    kind = s.BoundaryBelow.ToString(),
                    atDepthM = s.Base.Normalized!.Value,
                    note = s.BoundaryNote,
                    sectionId = s.Id,
                    datum = s.Top.Datum,
                }));
                CloseRun(s.BoundaryBelow);
                boundaryAbove = s.BoundaryBelow;
            }
        }
        CloseRun(BoundaryKind.None);
        evts.Add(E(Ev.SegmentationDerived, new
        {
            count = segments.Count,
            segments = segments.Select(s => new
            {
                s.Id, s.SectionIds, s.TopM, s.BaseM, s.DepthDatum,
                above = s.BoundaryAbove.ToString(),
                below = s.BoundaryBelow.ToString(),
            }),
        }));

        // --- active exclusions (latest adopted decision wins; conflicts kept as events) ---
        var excluded = new HashSet<string>();
        foreach (var grp in input.Decisions.GroupBy(d => d.AnchorId))
        {
            var hist = grp.OrderBy(d => d.At, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
            if (hist.Select(d => d.Excluded).Distinct().Count() > 1)
                evts.Add(E(Ev.ExclusionConflict, new
                {
                    anchorId = grp.Key,
                    decisions = hist.Select(d => new { d.Id, d.Excluded, d.Adopted, d.DecidedBy, d.At }),
                }));
            var lastAdopted = hist.LastOrDefault(d => d.Adopted);
            if (lastAdopted is { Excluded: true }) excluded.Add(grp.Key);
        }

        var active = normAnchors.Where(a => a.Depth.Normalized.HasValue && !excluded.Contains(a.Id)).ToList();
        return BuildModels(segments, active, input, diags, evts, ref seq, batchId, changedEntityIds);
    }

    private BuildResult BuildModels(List<Segment> segments, List<AgeAnchor> active, ModelInput input,
        List<Diagnostic> diags, List<DomainEvent> evts, ref long seq, string batchId, HashSet<string>? changedIds)
    {
        DomainEvent E(string kind, object payload) => NewEvent(kind, payload, batchId, ref seq);
        var candidates = new List<ModelCandidate>();
        var unsolvableSegment = false;
        var budgetExhausted = false;
        var inputDigest = Digest(new
        {
            ruleVersion = input.RuleVersion,
            sections = input.Sections.OrderBy(s => s.Id).Select(s => new { s.Id, top = s.Top.RawValue, s.Top.RawUnit, s.Top.Datum, bas = s.Base.RawValue, s.BoundaryBelow }),
            anchors = active.OrderBy(a => a.Id).Select(a => new
            {
                a.Id,
                depth = a.Depth.RawValue,
                unit = a.Depth.RawUnit,
                datum = a.Depth.Datum,
                dist = new { a.Distribution.Kind, a.Distribution.Mean, a.Distribution.Sd, a.Distribution.Min, a.Distribution.Max, n = a.Distribution.Samples?.Length },
                res = a.ReservoirOffset?.RawValue,
            }),
        });

        foreach (var seg in segments)
        {
            var inSeg = active
                .Where(a => a.Depth.Normalized!.Value >= seg.TopM - DepthEps
                         && a.Depth.Normalized!.Value <= seg.BaseM + DepthEps)
                .OrderBy(a => a.Depth.Normalized!.Value).ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            if (inSeg.Count == 0) continue;

            foreach (var a in inSeg)
            {
                evts.Add(E(Ev.DistributionPreserved, new
                {
                    anchorId = a.Id,
                    kind = a.Distribution.Kind,
                    q05 = a.Distribution.Quantile(0.05),
                    q50 = a.Distribution.Quantile(0.5),
                    q95 = a.Distribution.Quantile(0.95),
                    datum = a.Depth.Datum,
                    sampleCount = a.Distribution.Samples?.Length,
                    reservoirOffsetKa = a.ReservoirOffset?.Normalized,
                }));
                if (a.Distribution.IsLongTail())
                    evts.Add(E(Ev.LongTailDetected, new
                    {
                        anchorId = a.Id,
                        q05 = a.Distribution.Quantile(0.05),
                        q50 = a.Distribution.Quantile(0.5),
                        q95 = a.Distribution.Quantile(0.95),
                        skew = (a.Distribution.Quantile(0.95) - a.Distribution.Quantile(0.5))
                             / Math.Max(1e-9, a.Distribution.Quantile(0.5) - a.Distribution.Quantile(0.05)),
                    }));
            }

            // depth groups: equal normalized depth within epsilon (深度重复 / 并列锚点)
            var groups = new List<List<AgeAnchor>>();
            foreach (var a in inSeg)
            {
                var g = groups.LastOrDefault();
                if (g != null && Math.Abs(g[0].Depth.Normalized!.Value - a.Depth.Normalized!.Value) <= DepthEps)
                    g.Add(a);
                else
                    groups.Add(new List<AgeAnchor> { a });
            }
            foreach (var g in groups.Where(g => g.Count > 1))
            {
                evts.Add(E(Ev.DepthDuplicate, new
                {
                    segmentId = seg.Id,
                    depthM = g[0].Depth.Normalized!.Value,
                    datum = seg.DepthDatum,
                    anchorIds = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal),
                }));
                evts.Add(E(Ev.TieDetected, new
                {
                    segmentId = seg.Id,
                    depthM = g[0].Depth.Normalized!.Value,
                    orderings = Factorial(g.Count),
                    anchorIds = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal),
                }));
            }

            // monotonicity: age may only increase with depth inside a continuous segment
            AgeDistribution Shifted(AgeAnchor a) =>
                a.ReservoirOffset?.Normalized is double o ? a.Distribution.Shift(o) : a.Distribution;
            var inversions = new List<object>();
            for (var i = 0; i < groups.Count; i++)
                for (var j = i + 1; j < groups.Count; j++)
                    foreach (var sh in groups[i])
                        foreach (var dp in groups[j])
                        {
                            var (sLo, sHi) = Shifted(sh).Support();
                            var (dLo, dHi) = Shifted(dp).Support();
                            if (sLo > dHi) // shallower anchor is definitively older -> inversion
                                inversions.Add(new { shallower = sh.Id, deeper = dp.Id, shallowerMinKa = sLo, deeperMaxKa = dHi });
                        }
            if (inversions.Count > 0)
            {
                unsolvableSegment = true;
                evts.Add(E(Ev.InversionDetected, new { segmentId = seg.Id, inversions }));
                evts.Add(E(Ev.MonotonicityJudged, new { segmentId = seg.Id, verdict = "failed", inversions }));
                var rejected = new ModelCandidate
                {
                    Id = $"cand_{seg.Id}_{inputDigest[..8]}_rejected",
                    SegmentId = seg.Id,
                    AnchorOrder = inSeg.Select(a => a.Id).ToList(),
                    Rates = Array.Empty<RateInterval>(),
                    Band = Array.Empty<(double, double, double, double)>(),
                    Status = "rejected",
                    RejectReason = "倒置层段：不断层段内年龄无法随深度单调",
                    InputDigest = inputDigest,
                    RuleVersion = input.RuleVersion,
                    Parents = inSeg.Select(a => a.Id).Concat(seg.SectionIds).ToList(),
                };
                candidates.Add(rejected);
                continue;
            }

            evts.Add(E(Ev.MonotonicityJudged, new { segmentId = seg.Id, verdict = "ok", groups = groups.Count }));

            // enumerate legal orderings: ties permute freely; every equally-good model is kept
            var orderings = new List<List<string>> { new() };
            foreach (var g in groups)
            {
                var perms = Permutations(g.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToList());
                var next = new List<List<string>>();
                foreach (var prefix in orderings)
                    foreach (var p in perms)
                    {
                        var combined = new List<string>(prefix);
                        combined.AddRange(p);
                        next.Add(combined);
                    }
                orderings = next;
                if (orderings.Count > input.CandidateBudget) break;
            }
            if (orderings.Count > input.CandidateBudget)
            {
                budgetExhausted = true;
                evts.Add(E(Ev.BudgetExhausted, new
                {
                    segmentId = seg.Id,
                    required = orderings.Count,
                    budget = input.CandidateBudget,
                }));
                continue;
            }

            var segCandidates = new List<ModelCandidate>();
            foreach (var order in orderings)
            {
                var byId = inSeg.ToDictionary(a => a.Id);
                var chain = order.Select(id => byId[id]).ToList();
                var (rates, band) = DeriveRatesAndBand(chain, Shifted);
                segCandidates.Add(new ModelCandidate
                {
                    Id = $"cand_{seg.Id}_{inputDigest[..8]}_{string.Join("-", order)}",
                    SegmentId = seg.Id,
                    AnchorOrder = order,
                    Rates = rates,
                    Band = band,
                    Status = "admissible",
                    InputDigest = inputDigest,
                    RuleVersion = input.RuleVersion,
                    Parents = chain.Select(a => a.Id).Concat(seg.SectionIds).ToList(),
                });
            }
            candidates.AddRange(segCandidates);
            evts.Add(E(Ev.CandidatesRetained, new
            {
                segmentId = seg.Id,
                count = segCandidates.Count,
                candidateIds = segCandidates.Select(c => c.Id),
                equallyGood = segCandidates.Count > 1,
            }));
        }

        var affected = changedIds is null
            ? segments.Select(s => s.Id).ToList()
            : segments.Where(s => s.SectionIds.Any(changedIds.Contains)
                || active.Any(a => changedIds.Contains(a.Id)
                    && a.Depth.Normalized!.Value >= s.TopM - DepthEps
                    && a.Depth.Normalized!.Value <= s.BaseM + DepthEps))
                .Select(s => s.Id).ToList();
        evts.Add(E(Ev.LocalRecompute, new
        {
            changedEntityIds = changedIds?.OrderBy(x => x, StringComparer.Ordinal),
            affectedSegments = affected,
            unaffectedSegments = segments.Select(s => s.Id).Except(affected).ToList(),
        }));
        evts.Add(E(Ev.ModelDerived, new
        {
            inputDigest,
            ruleVersion = input.RuleVersion,
            candidateCount = candidates.Count(c => c.Status == "admissible"),
            rejectedCount = candidates.Count(c => c.Status == "rejected"),
            diagnostics = diags.Count,
        }));

        var status = budgetExhausted ? BuildStatus.BudgetExhausted
            : unsolvableSegment ? BuildStatus.Unsolvable
            : BuildStatus.Ok;
        return new BuildResult(status, segments, candidates, diags, evts);
    }

    private static (List<RateInterval>, List<(double, double, double, double)>) DeriveRatesAndBand(
        List<AgeAnchor> chain, Func<AgeAnchor, AgeDistribution> shifted)
    {
        var rates = new List<RateInterval>();
        var band = new List<(double, double, double, double)>();
        foreach (var a in chain)
        {
            var d = shifted(a);
            band.Add((a.Depth.Normalized!.Value, d.Quantile(0.05), d.Quantile(0.5), d.Quantile(0.95)));
        }
        for (var i = 0; i + 1 < chain.Count; i++)
        {
            var up = chain[i];
            var dn = chain[i + 1];
            var dz = dn.Depth.Normalized!.Value - up.Depth.Normalized!.Value;
            var du = shifted(up);
            var dd = shifted(dn);
            var ageLo = Math.Max(1e-6, dd.Quantile(0.05) - du.Quantile(0.95));
            var ageMid = Math.Max(1e-6, dd.Quantile(0.5) - du.Quantile(0.5));
            var ageHi = Math.Max(1e-6, dd.Quantile(0.95) - du.Quantile(0.05));
            rates.Add(new RateInterval(up.Id, dn.Id,
                up.Depth.Normalized!.Value, dn.Depth.Normalized!.Value,
                dz / ageHi, dz / ageMid, dz / ageLo, "m/ka"));
        }
        return (rates, band);
    }

    private static List<List<string>> Permutations(IReadOnlyList<string> items)
    {
        var result = new List<List<string>>();
        var work = items.ToArray();
        void Rec(int k)
        {
            if (k == work.Length) { result.Add(work.ToList()); return; }
            for (var i = k; i < work.Length; i++)
            {
                (work[k], work[i]) = (work[i], work[k]);
                Rec(k + 1);
                (work[k], work[i]) = (work[i], work[k]);
            }
        }
        Rec(0);
        return result;
    }

    private static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    private DomainEvent NewEvent(string kind, object payload, string batchId, ref long seq)
    {
        seq++;
        return new DomainEvent(seq, $"ev-{seq:D6}", kind,
            JsonSerializer.Serialize(payload, JsonOpts), DateTime.UtcNow.ToString("o"), Actor, batchId);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Digest<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }
}
