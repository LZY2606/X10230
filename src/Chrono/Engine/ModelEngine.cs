using Chrono.Domain;

namespace Chrono.Engine;

/// <summary>
/// 深度年龄模型引擎（纯领域代码，可被测试直接调用）。
///
/// 关键规则：
///  - 年龄只能在“不断层段”中单调；深度缺口(Gap)与不整合(Unconformity)是显式边界，绝不跨边界插值。
///  - 一个锚点的分布不被均值替代：速率/不确定度带全部由分布样本传播。
///  - 并列锚点(tie group)产生多套候选；等优候选全部保留，按稳定身份排序。
/// </summary>
public sealed class ModelEngine
{
    private readonly MaterialState _state;
    private readonly Calibrator _calibrator;

    public ModelEngine(MaterialState state)
    {
        _state = state;
        _calibrator = new Calibrator(state);
    }

    public DepthAgeView Build(string? onlySegmentId = null)
    {
        var segments = BuildSegments();
        var diagnostics = new List<DiagnosticView>();
        var rates = new List<RateInterval>();
        var bands = new List<UncertaintyBand>();
        var lineage = new List<string>();
        var outcome = nameof(CommandOutcome.Accepted);
        var candidateTotal = 0;

        foreach (var seg in segments)
        {
            if (onlySegmentId is not null && seg.SegmentId != onlySegmentId) continue;
            var result = BuildSegment(seg);
            seg.Candidates.AddRange(result.Candidates);
            seg.Diagnostics.AddRange(result.Diagnostics);
            seg.States.AddRange(result.States);
            seg.HasDuplicateDepthConflict = result.HasDuplicate;
            seg.HasInversion = result.HasInversion;
            seg.InversionLegitimized = result.InversionLegitimized;
            candidateTotal += seg.Candidates.Count;
            rates.AddRange(result.Rates);
            bands.AddRange(result.Bands);
            lineage.AddRange(result.Lineage);
            if (result.Outcome == nameof(CommandOutcome.BudgetExhausted))
                outcome = nameof(CommandOutcome.BudgetExhausted);
        }

        var stateHash = Canonical.Digest(new
        {
            datum = _state.Datum?.Id,
            sections = _state.Sections.Keys.OrderBy(k => k),
            boundaries = _state.Boundaries.Keys.OrderBy(k => k),
            anchors = _state.Anchors.Keys.OrderBy(k => k),
            versions = _state.AnchorSourceVersion.OrderBy(kv => kv.Key),
            exclusions = _state.Exclusions.Keys.OrderBy(k => k),
            inversions = _state.InversionDecisions.OrderBy(k => k),
            parameters = _state.Params,
        });

        return new DepthAgeView
        {
            StateHash = stateHash,
            ParamsVersion = _state.Params.Version,
            Segments = segments.Where(s => onlySegmentId is null || s.SegmentId == onlySegmentId).ToList(),
            RateIntervals = rates,
            UncertaintyBands = bands,
            Diagnostics = diagnostics,
            CandidateLineage = lineage,
            Outcome = outcome,
        };
    }

    private List<SegmentView> BuildSegments()
    {
        var boundaries = _state.Boundaries.Values
            .Where(b => b.Depth.Value.HasValue)
            .OrderBy(b => b.Depth.Value!.Value)
            .ToList();
        var sections = _state.Sections.Values
            .Where(s => s.Top.Value.HasValue && s.Base.Value.HasValue)
            .OrderBy(s => s.Top.Value!.Value)
            .ToList();

        var topDepth = sections.Count > 0 ? sections.First().Top.Value!.Value : 0;
        var baseDepth = sections.Count > 0 ? sections.Max(s => s.Base.Value!.Value) : 0;

        var hard = boundaries.Where(b => b.Kind is BoundaryKind.Gap or BoundaryKind.Unconformity or BoundaryKind.Fault).ToList();
        var cuts = new List<(double depth, BoundaryKind kind, string id)> { (topDepth, BoundaryKind.Contact, "top") };
        cuts.AddRange(hard.Select(b => (b.Depth.Value!.Value, b.Kind, b.Id)));
        cuts.Add((baseDepth, BoundaryKind.Contact, "base"));
        cuts = cuts.DistinctBy(c => (c.Item1, c.Item3)).OrderBy(c => c.Item1).ToList();

        var result = new List<SegmentView>();
        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var (upper, upperKind, upperId) = cuts[i];
            var (lower, lowerKind, lowerId) = cuts[i + 1];
            if (lower - upper < 1e-9) continue;
            var isFaulted = upperKind == BoundaryKind.Fault || lowerKind == BoundaryKind.Fault;
            var anchorIds = _state.Anchors.Values
                .Where(a => a.Depth.Value is { } d && d > upper - 1e-9 && d < lower + 1e-9 && _state.IsAnchorActive(a.Id))
                .OrderBy(a => a.Depth.Value!.Value).ThenBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => a.Id)
                .ToList();
            var id = Canonical.ShortId("seg", $"{upper:F4}|{lower:F4}|{upperId}|{lowerId}");
            result.Add(new SegmentView
            {
                SegmentId = id,
                Label = $"段 {upper:F2}–{lower:F2} m",
                TopDepth = upper,
                BaseDepth = lower,
                UpperBoundary = upperId,
                LowerBoundary = lowerId,
                IsFaulted = isFaulted,
                AnchorIds = anchorIds,
                Candidates = new(),
                Diagnostics = new(),
                States = new(),
            });
        }
        return result;
    }

    private sealed record SegmentResult(
        List<CandidateView> Candidates, List<DiagnosticView> Diagnostics, List<string> States,
        List<RateInterval> Rates, List<UncertaintyBand> Bands, List<string> Lineage,
        bool HasDuplicate, bool HasInversion, bool InversionLegitimized, string Outcome);

    private SegmentResult BuildSegment(SegmentView seg)
    {
        var diags = new List<DiagnosticView>();
        var states = new List<string>();
        var rates = new List<RateInterval>();
        var bands = new List<UncertaintyBand>();
        var lineage = new List<string>();
        var outcome = nameof(CommandOutcome.Accepted);

        var anchors = seg.AnchorIds.Select(id => _state.Anchors[id]).ToList();

        var hasDuplicate = false;
        for (var i = 0; i < anchors.Count; i++)
            for (var j = i + 1; j < anchors.Count; j++)
            {
                var sameTie = !string.IsNullOrEmpty(anchors[i].TieGroup) &&
                              anchors[i].TieGroup == anchors[j].TieGroup;
                var diff = Math.Abs(anchors[i].Depth.Value!.Value - anchors[j].Depth.Value!.Value);
                if (diff <= _state.Params.DuplicateDepthToleranceMetres && !sameTie)
                {
                    hasDuplicate = true;
                    diags.Add(new("DEPTH_DUPLICATE", "error",
                        $"深度重复且非并列组：{anchors[i].Id} 与 {anchors[j].Id} 在容差 {_state.Params.DuplicateDepthToleranceMetres} m 内",
                        anchors[j].Id, "depth", $"segment {seg.SegmentId}"));
                }
            }
        if (hasDuplicate) states.Add("depth-duplicate-conflict");

        var tieGroups = anchors
            .Where(a => !string.IsNullOrEmpty(a.TieGroup))
            .GroupBy(a => a.TieGroup!)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Id).OrderBy(x => x).ToList());

        var combinations = EnumerateChoices(tieGroups);
        if (combinations.Count > _state.Params.MaxCandidates)
        {
            diags.Add(new("BUDGET_EXHAUSTED", "error",
                $"候选组合数 {combinations.Count} 超过预算 {_state.Params.MaxCandidates}",
                seg.SegmentId, "candidates", null));
            states.Add("budget-exhausted");
            outcome = nameof(CommandOutcome.BudgetExhausted);
        }

        var distributions = anchors.ToDictionary(a => a.Id, a => _calibrator.Resolve(a));
        var hasInversion = anchors.Any(a => a.InvertedFlag);
        var inversionLegit = !_state.Params.InversionRequiresDecision ||
                             _state.InversionDecisions.Contains(seg.SegmentId);
        if (hasInversion)
        {
            states.Add(inversionLegit ? "inversion-acknowledged" : "inversion-unresolved");
            diags.Add(new(inversionLegit ? "INVERSION_OK" : "INVERSION_UNRESOLVED",
                inversionLegit ? "info" : "error",
                inversionLegit ? "倒置层段已有显式人工决定" : "倒置层段需要显式人工决定；年龄顺序保持倒置而非强行单调",
                seg.SegmentId, "polarity", null));
        }

        var candidates = new List<CandidateView>();
        var chosenCount = 0;
        foreach (var choice in combinations)
        {
            chosenCount++;
            if (chosenCount > _state.Params.MaxCandidates) break;
            var excluded = anchors.Where(a => choice.Contains(a.Id) == false).Select(a => a.Id).ToList();
            var chosen = anchors.Where(a => choice.Contains(a.Id)).ToList();
            var (candidate, moreDiags, moreStates, segRates, segBands, moreLineage) =
                EvaluateCandidate(seg, chosen, excluded, distributions, hasDuplicate, hasInversion, inversionLegit);
            candidates.Add(candidate);
            diags.AddRange(moreDiags);
            states.AddRange(moreStates);
            rates.AddRange(segRates);
            bands.AddRange(segBands);
            lineage.AddRange(moreLineage);
        }

        if (candidates.Count > 0)
        {
            var best = candidates.Min(c => c.Score);
            var tol = 1e-9;
            var optimalCount = candidates.Count(c => Math.Abs(c.Score - best) <= tol);
            foreach (var c in candidates)
            {
                c.IsOptimal = Math.Abs(c.Score - best) <= tol;
                if (c.IsOptimal) c.Adopted = true;
            }
            if (optimalCount > 1)
            {
                states.Add("tied-optimal-retained");
                diags.Add(new("TIED_OPTIMAL", "info",
                    $"{optimalCount} 个等优模型全部保留，不做任意择一", seg.SegmentId, "candidates", null));
            }
        }

        if (seg.IsFaulted)
        {
            states.Add("faulted-no-monotonic-constraint");
            diags.Add(new("FAULTED_SEGMENT", "info", "断层段不施加上升单调约束", seg.SegmentId, "boundary", null));
        }
        if (anchors.Count < 2 && !hasDuplicate)
        {
            states.Add("under-constrained-no-rate");
            diags.Add(new("UNDER_CONSTRAINED", "warning",
                "不断层段内有效锚点不足两个，无法限定速率，不生成插值", seg.SegmentId, "anchors", null));
        }

        return new SegmentResult(candidates, diags, states.Distinct().ToList(), rates, bands,
            lineage.Distinct().ToList(), hasDuplicate, hasInversion, inversionLegit, outcome);
    }

    private static List<HashSet<string>> EnumerateChoices(Dictionary<string, List<string>> tieGroups)
    {
        var result = new List<HashSet<string>> { new() };
        foreach (var group in tieGroups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var next = new List<HashSet<string>>();
            foreach (var baseSet in result)
                foreach (var pick in group.Value.OrderBy(x => x, StringComparer.Ordinal))
                {
                    var set = new HashSet<string>(baseSet) { pick };
                    next.Add(set);
                }
            result = next;
        }
        // 非并列锚点总是被选入（调用方补齐）
        return result;
    }

    private (CandidateView, List<DiagnosticView>, List<string>, List<RateInterval>, List<UncertaintyBand>, List<string>)
        EvaluateCandidate(SegmentView seg, List<AgeAnchor> chosen, List<string> declined,
        Dictionary<string, AgeDistribution> distributions, bool hasDuplicate, bool hasInversion, bool inversionLegit)
    {
        var diags = new List<DiagnosticView>();
        var states = new List<string>();
        var rates = new List<RateInterval>();
        var bands = new List<UncertaintyBand>();
        var lineage = new List<string>();

        var ordered = chosen.OrderBy(a => a.Depth.Value!.Value).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var chosenIds = ordered.Select(a => a.Id).ToList();
        var dependsOn = ordered.Select(a => $"{a.Id}@{(_state.AnchorSourceVersion.TryGetValue(a.Id, out var v) ? v : "v0")}").ToList();
        var inputDigest = Canonical.Digest(new
        {
            segment = seg.SegmentId,
            anchors = dependsOn,
            parameters = _state.Params.Version,
        });
        var candidateId = Canonical.ShortId("cand", $"{seg.SegmentId}|{string.Join(",", chosenIds)}|{inputDigest[..8]}");

        var monotonic = true;
        var tailAnchors = new List<string>();
        var slopes = new List<double>();
        var residualAbs = new List<double>();

        if (ordered.Count >= 2 && !hasDuplicate)
        {
            var n = _state.Params.SamplesPerAnchor;
            var draws = ordered.ToDictionary(a => a.Id, a =>
            {
                var seed = Canonical.SeedFrom($"{candidateId}|{a.Id}|{_state.Params.Version}");
                var dist = distributions[a.Id];
                var rng = new DeterministicRng(seed);
                var list = new List<double>(n);
                for (var i = 0; i < n; i++) list.Add(dist.Sample(rng));
                return list;
            });

            foreach (var a in ordered)
            {
                var vals = draws[a.Id];
                var (q05, q50, q95) = Quantiles(vals);
                var tail = TailFraction(vals, q50, _state.Params.TailThresholdSigma);
                if (tail > 0.02) tailAnchors.Add(a.Id);
                bands.Add(new UncertaintyBand
                {
                    BandId = Canonical.ShortId("band", $"{candidateId}|{a.Id}"),
                    SegmentId = seg.SegmentId,
                    CandidateId = candidateId,
                    Depth = a.Depth.Value!.Value,
                    AgeMedian = q50,
                    AgeLow = q05,
                    AgeHigh = q95,
                    TimeBasis = "cal BP yr",
                });
            }

            for (var k = 0; k < n; k++)
            {
                var allIncreasing = true;
                for (var i = 1; i < ordered.Count; i++)
                {
                    var younger = draws[ordered[i - 1].Id][k];
                    var older = draws[ordered[i].Id][k];
                    if (seg.IsFaulted) { /* 断层段不约束 */ }
                    else if (hasInversion && ordered[i].InvertedFlag) { /* 显式倒置段：保持倒置 */ }
                    else if (!(older > younger)) { allIncreasing = false; break; }
                }
                if (!allIncreasing) monotonic = false;
            }

            // 速率：逐相邻锚点对，整体由样本传播
            for (var i = 1; i < ordered.Count; i++)
            {
                var a0 = ordered[i - 1];
                var a1 = ordered[i];
                var pairSlopes = new List<double>();
                for (var k = 0; k < n; k++)
                {
                    var dd = a1.Depth.Value!.Value - a0.Depth.Value!.Value;
                    if (Math.Abs(dd) < 1e-9) continue;
                    var s = (draws[a1.Id][k] - draws[a0.Id][k]) / dd;
                    if (seg.IsFaulted || (hasInversion && a1.InvertedFlag)) pairSlopes.Add(Math.Abs(s));
                    else pairSlopes.Add(s);
                }
                if (pairSlopes.Count == 0) continue;
                var (rl, rm, rh) = Quantiles(pairSlopes);
                slopes.Add(rm);
                var invertedHere = hasInversion && a1.InvertedFlag;
                if (!seg.IsFaulted && !invertedHere && rm < 0)
                    monotonic = false;
                rates.Add(new RateInterval
                {
                    RateId = Canonical.ShortId("rate", $"{candidateId}|{a0.Id}|{a1.Id}"),
                    SegmentId = seg.SegmentId,
                    CandidateId = candidateId,
                    FromDepth = a0.Depth.Value!.Value,
                    ToDepth = a1.Depth.Value!.Value,
                    RateMedian = rm,
                    RateLow = rl,
                    RateHigh = rh,
                });
            }

            // 拟合残差（中位年龄对深度线性拟合）
            if (ordered.Count >= 2)
            {
                var med = ordered.Select(a => (d: a.Depth.Value!.Value, age: Quantiles(draws[a.Id]).Item2)).ToList();
                var meanD = med.Average(x => x.d);
                var meanA = med.Average(x => x.age);
                var denom = med.Sum(x => (x.d - meanD) * (x.d - meanD));
                var slope = denom > 1e-12 ? med.Sum(x => (x.d - meanD) * (x.age - meanA)) / denom : 0;
                var intercept = meanA - slope * meanD;
                residualAbs.AddRange(med.Select(x => Math.Abs(x.age - (slope * x.d + intercept))));
            }
        }

        if (!monotonic && !seg.IsFaulted && !(hasInversion && inversionLegit))
        {
            states.Add("non-monotonic-invalid");
            diags.Add(new("NON_MONOTONIC", "error",
                "不断层段内年龄未随深度单调增加（年龄只能在不断层段中单调）",
                candidateId, "ordering", seg.SegmentId));
        }
        else if (!monotonic && hasInversion && inversionLegit)
        {
            states.Add("inversion-preserved");
        }
        if (tailAnchors.Count > 0)
        {
            states.Add("long-tail-distribution");
            diags.Add(new("LONG_TAIL", "warning",
                $"锚点 {string.Join(",", tailAnchors)} 年龄分布长尾保留，未用均值替代",
                candidateId, "distribution", null));
        }
        if (hasDuplicate) states.Add("blocked-by-duplicate-depth");

        var score = (monotonic || seg.IsFaulted ? 0 : 1_000_000)
                  + (residualAbs.Count > 0 ? residualAbs.Median() : 0);
        lineage.Add($"{candidateId} <- {string.Join("+", chosenIds.DefaultIfEmpty())} [{_state.Params.Version}]");

        var candidate = new CandidateView
        {
            CandidateId = candidateId,
            SegmentId = seg.SegmentId,
            ChosenAnchorIds = chosenIds,
            DeclinedAnchorIds = declined.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Score = score,
            IsOptimal = false,
            Monotonic = monotonic || seg.IsFaulted,
            SlopeYearsPerMetre = slopes.Count > 0 ? slopes.Median() : 0,
            SlopeLow = rates.Count > 0 ? rates.Min(r => r.RateLow) : 0,
            SlopeHigh = rates.Count > 0 ? rates.Max(r => r.RateHigh) : 0,
            FitResidualMedian = residualAbs.Count > 0 ? residualAbs.Median() : 0,
            InputDigest = inputDigest,
            ParamsVersion = _state.Params.Version,
            ParentDependsOn = dependsOn,
        };
        return (candidate, diags, states, rates, bands, lineage);
    }

    private static (double q05, double q50, double q95) Quantiles(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        double Q(double p) => sorted[(int)Math.Clamp(Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
        return (Q(0.05), Q(0.5), Q(0.95));
    }

    private static double TailFraction(List<double> values, double median, double sigmaThreshold)
    {
        if (values.Count < 4) return 0;
        var sigma = Math.Sqrt(values.Average(v => (v - median) * (v - median)));
        if (sigma < 1e-9) return 0;
        return values.Count(v => Math.Abs(v - median) > sigmaThreshold * sigma) / (double)values.Count;
    }
}

internal static class LinqExtensions
{
    public static double Median(this IEnumerable<double> source)
    {
        var list = source.OrderBy(x => x).ToList();
        if (list.Count == 0) return 0;
        return list.Count % 2 == 0
            ? (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2
            : list[list.Count / 2];
    }
}
