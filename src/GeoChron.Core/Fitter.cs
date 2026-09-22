namespace GeoChron.Core;

public enum FitStrategyKind { StrictLinear, Isotonic, ExcludeInversion, InsertUnconformity }

public sealed record FitSpec(FitStrategyKind Kind, IReadOnlyDictionary<string, string> Detail,
    IReadOnlySet<string> UsedAnchorIds, IReadOnlySet<string> InsertedGapIds,
    IReadOnlyList<Section>? OverrideSections = null);

public sealed record FittedCandidate(CandidateModel Model, IReadOnlyList<Finding> Findings, bool Legal,
    IReadOnlyList<Diagnostic> Diagnostics, double MonotoneViolationRate);

/// <summary>
/// 服务端/存储层可直接调用的拟合器：业务规则不在浏览器中。
/// 所有不确定度都以分位带表达；任何锚点都不得用均值替代分布。
/// </summary>
public sealed class Fitter(ComputeBudgetLedger ledger)
{
    public FittedCandidate Fit(CasePack pack, ModelParams p, IReadOnlyList<Section> sections,
        PreparedConstraints prepared, FitSpec spec, string inputSummary)
    {
        ledger.BeginCandidate();
        var findings = new List<Finding>();
        var diags = new List<Diagnostic>();
        var rates = new List<RateInterval>();
        var band = new List<BandPoint>();
        var totalViol = 0.0;
        var violCount = 0;
        var legal = true;
        var rejectReasons = new List<string>();
        var seedBase = DeriveSeed(spec, inputSummary);

        var effectiveSections = spec.OverrideSections ?? sections;
        foreach (var sec in effectiveSections)
        {
            var cons = prepared.All
                .Where(c => c.DepthCm >= sec.TopCm - 1e-9 && c.DepthCm <= sec.BottomCm + 1e-9 &&
                            spec.UsedAnchorIds.Contains(c.Id))
                .OrderBy(c => c.DepthCm).ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            if (cons.Count < 2)
            {
                diags.Add(new Diagnostic("F001", $"sections[{sec.Id}]",
                    $"层段 {sec.Id} 可用约束不足 2 个（{cons.Count}），无法唯一决定年龄曲线，保留其他合法候选", "warning"));
                continue;
            }

            var depths = cons.Select(c => c.DepthCm).ToArray();
            if (!ledger.TryAcquireDraws((long)cons.Count * p.McBudget))
                throw new BudgetExhaustedException();
            var drawSets = cons.Select((c, i) =>
            {
                var seed = seedBase ^ (ulong)i * 0x9E3779B97F4A7C15UL ^ (ulong)sec.Index * 0xD1B54A32D192ED03UL;
                var s = c.Distribution.Sample(p.McBudget, seed);
                return ConstraintPreparer.ApplyReservoir(s, pack, c);
            }).ToArray();

            // 并列深度检测（等优 tie）：相同深度的锚点之间不能定义年龄顺序。
            for (var i = 1; i < depths.Length; i++)
                if (depths[i] - depths[i - 1] <= 1e-9)
                    findings.Add(new Finding(FindingKind.TieGroup,
                        new[] { cons[i - 1].Id, cons[i].Id }, sec.Id, depths[i],
                        $"并列锚点 {cons[i - 1].Id}/{cons[i].Id}：同深度不强制年龄先后，作为并列 tie 参与保序拟合"));

            // 逐次抽样拟合，统计违例率（strict 策略下违例即非法）。
            var n = p.McBudget;
            var viol = 0;
            var curves = new List<double[]>();
            var grid = BuildGrid(sec, p.GridStepCm);
            var bandSamples = new double[grid.Length][];
            for (var g = 0; g < grid.Length; g++) bandSamples[g] = new double[n];

            for (var d = 0; d < n; d++)
            {
                var ages = drawSets.Select((s, i) => s![d]).ToArray();
                var (curve, inverted) = FitOnce(depths, ages, p, spec, sec, findings, rejectReasons);
                if (inverted) viol++;
                for (var g = 0; g < grid.Length; g++)
                    bandSamples[g][d] = Interp(depths, curve, grid[g]);
            }
            totalViol += (double)viol / n; violCount++;
            if (spec.Kind == FitStrategyKind.StrictLinear && viol > 0)
            {
                legal = false;
                rejectReasons.Add($"层段 {sec.Id} 严格线性单调在 {viol}/{n} 次抽样中出现倒置");
            }

            // 速率区间：逐抽样计算相邻锚点间斜率。
            for (var i = 1; i < cons.Count; i++)
            {
                var segRates = new double[n];
                var denom = depths[i] - depths[i - 1];
                if (denom <= 1e-9)
                {
                    rates.Add(new RateInterval(sec.Id, depths[i - 1], depths[i], null, null, null, "cm/ka",
                        "并列深度：沉积速率不可唯一估计"));
                    continue;
                }
                for (var d = 0; d < n; d++)
                {
                    var dd = denom;
                    segRates[d] = dd / Math.Max(1e-9, drawSets[i]![d] - drawSets[i - 1]![d]);
                }
                Array.Sort(segRates);
                rates.Add(new RateInterval(sec.Id, depths[i - 1], depths[i],
                    QuantileSorted(segRates, .025), QuantileSorted(segRates, .5), QuantileSorted(segRates, .975), "cm/ka"));
            }

            // 长尾检测：95 分位相对中位数的偏度，提示不能以均值表达。
            var first = bandSamples[bandSamples.Length / 2];
            var sortedFirst = (double[])first.Clone(); Array.Sort(sortedFirst);
            var med = QuantileSorted(sortedFirst, .5);
            var q975 = QuantileSorted(sortedFirst, .975);
            var q025 = QuantileSorted(sortedFirst, .025);
            if (med > 0 && (q975 - med) > 3 * (med - q025))
                findings.Add(new Finding(FindingKind.LongTail, cons.Select(c => c.Id).ToList(), sec.Id, null,
                    $"层段 {sec.Id} 年龄分布呈长尾（q975={q975:0.##} 相对中位 {med:0.##}），只能用分位带呈现，不能均值替代"));

            for (var g = 0; g < grid.Length; g++)
            {
                var col = (double[])bandSamples[g].Clone(); Array.Sort(col);
                var qs = p.TailQuantiles.Distinct().OrderBy(x => x).ToDictionary(
                    qq => qq, qq => QuantileSorted(col, qq));
                band.Add(new BandPoint(sec.Id, grid[g], qs));
            }
        }

        var used = spec.UsedAnchorIds.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var excluded = prepared.All.Select(c => c.Id).Except(used).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var violRate = violCount == 0 ? 0 : totalViol / violCount;
        var score = Score(violRate, spec);
        var id = $"cand-{StableIds.Hash(inputSummary + "|" + spec.Kind + "|" + string.Join(",", used))}";
        var detail = spec.Detail;
        var model = new CandidateModel
        {
            Id = id, Rank = 0, Status = legal ? "legal" : "rejected",
            Strategy = spec.Kind.ToString(), StrategyDetail = detail,
            Score = score, EqualOptimal = false,
            RejectionReason = legal ? null : string.Join("；", rejectReasons),
            ParentDependencies = BuildParents(pack, p, spec),
            ParamsVersion = p.Version, InputSummary = inputSummary,
            UsedAnchorIds = used, ExcludedAnchorIds = excluded,
            InsertedGapIds = spec.InsertedGapIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            RateIntervals = rates, Band = band, MonotoneViolationRate = violRate
        };
        return new FittedCandidate(model, findings, legal, diags, violRate);
    }

    private static (double[] curve, bool inverted) FitOnce(double[] depths, double[] ages, ModelParams p,
        FitSpec spec, Section sec, List<Finding> findings, List<string> rejects)
    {
        switch (spec.Kind)
        {
            case FitStrategyKind.StrictLinear:
            {
                var inv = false;
                for (var i = 1; i < ages.Length; i++)
                    if (ages[i] < ages[i - 1] - p.TieToleranceKa && depths[i] > depths[i - 1] + 1e-9)
                    {
                        inv = true;
                        findings.Add(new Finding(FindingKind.InvertedInterval,
                            new[] { $"{sec.Id}" }, sec.Id, depths[i - 1],
                            $"倒置层段区间 {depths[i - 1]:0.##}-{depths[i]:0.##} cm：年龄 {ages[i - 1]:0.##}→{ages[i]:0.##} ka 变年轻"));
                    }
                return (ages, inv);
            }
            case FitStrategyKind.Isotonic:
            {
                var w = Enumerable.Repeat(1.0, ages.Length).ToArray();
                var blocks = Isotonic.NonDecreasing(ages, w);
                var curve = new double[ages.Length];
                foreach (var (s, e, v) in blocks) for (var i = s; i <= e; i++) curve[i] = v;
                return (curve, false);
            }
            case FitStrategyKind.ExcludeInversion:
            {
                // 删除最“年轻得反常”的锚点后再保序；删除对象必须已记录为策略输入。
                var keep = ages.ToArray();
                return (IsotonicCurve(keep), false);
            }
            case FitStrategyKind.InsertUnconformity:
                return (IsotonicCurve(ages), false);
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static double[] IsotonicCurve(double[] ages)
    {
        var blocks = Isotonic.NonDecreasing(ages, Enumerable.Repeat(1.0, ages.Length).ToArray());
        var curve = new double[ages.Length];
        foreach (var (s, e, v) in blocks) for (var i = s; i <= e; i++) curve[i] = v;
        return curve;
    }

    private static double[] BuildGrid(Section sec, double stepCm)
    {
        if (stepCm <= 0) stepCm = Math.Max(1, (sec.BottomCm - sec.TopCm) / 40);
        var pts = new List<double>();
        for (var d = sec.TopCm; d < sec.BottomCm - 1e-9; d += stepCm) pts.Add(Math.Round(d, 6));
        pts.Add(sec.BottomCm);
        return pts.Distinct().OrderBy(d => d).ToArray();
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 1; i < xs.Length; i++)
            if (x <= xs[i])
            {
                var t = (x - xs[i - 1]) / Math.Max(1e-9, xs[i] - xs[i - 1]);
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        return ys[^1];
    }

    private static double QuantileSorted(double[] s, double q)
    {
        var idx = Math.Clamp((int)Math.Round(q * (s.Length - 1)), 0, s.Length - 1);
        return s[idx];
    }

    private static double Score(double violRate, FitSpec spec) =>
        violRate * 1000 + spec.Kind switch
        {
            FitStrategyKind.StrictLinear => 0,
            FitStrategyKind.Isotonic => 5,
            FitStrategyKind.InsertUnconformity => 20,
            FitStrategyKind.ExcludeInversion => 40,
            _ => 100
        };

    private static List<string> BuildParents(CasePack pack, ModelParams p, FitSpec spec)
    {
        var parents = new List<string> { $"params:v{p.Version}", $"datum:{pack.DepthDatum.Id}" };
        parents.AddRange(spec.UsedAnchorIds.OrderBy(x => x).Select(x => $"anchor:{x}"));
        parents.AddRange(spec.InsertedGapIds.OrderBy(x => x).Select(x => $"gap:{x}"));
        return parents;
    }

    private static ulong DeriveSeed(FitSpec spec, string inputSummary)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(inputSummary + spec.Kind));
        return BitConverter.ToUInt64(bytes, 0);
    }
}

public sealed class BudgetExhaustedException : Exception;

public sealed class ComputeBudgetLedger(long drawBudget, int candidateBudget)
{
    private long _mcUsed;
    private long _candidateStart;
    private int _candidates;
    public long McUsed => _mcUsed;
    public int CandidatesEnumerated => _candidates;
    public bool CandidateExhausted { get; private set; }
    public void BeginCandidate() => _candidateStart = _mcUsed;
    public bool TryAcquireDraws(long draws)
    {
        if (_mcUsed + draws > drawBudget) return false;
        _mcUsed += draws;
        return true;
    }
    public void RollbackCandidate() => _mcUsed = _candidateStart;
    public bool TryEnumerateCandidate()
    {
        if (_candidates >= candidateBudget) { CandidateExhausted = true; return false; }
        _candidates++;
        return true;
    }
}
