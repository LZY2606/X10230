using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel.Core;

/// <summary>引擎终止原因三态：语义无解与预算耗尽必须是不同结果（R7）。</summary>
public enum EngineOutcome { Success, DegradedSuccess, SemanticUnresolvable, BudgetExhausted }

/// <summary>候选年龄-深度模型（一个段内的一种合法锚点组合）。</summary>
public sealed class CandidateModel
{
    [JsonPropertyName("modelId")] public string ModelId { init; get; } = "";
    [JsonPropertyName("segmentId")] public string SegmentId { init; get; } = "";
    [JsonPropertyName("anchorIds")] public List<string> AnchorIds { init; get; } = new();
    [JsonPropertyName("optionFingerprints")] public List<string> OptionFingerprints { init; get; } = new();
    [JsonPropertyName("rateKaperM")] public double RateKaperM { init; get; }
    [JsonPropertyName("rateP05")] public double RateP05 { init; get; }
    [JsonPropertyName("rateP95")] public double RateP95 { init; get; }
    [JsonPropertyName("slopeMisfit")] public double Misfit { init; get; }
    [JsonPropertyName("valid")] public bool Valid { init; get; }
    [JsonPropertyName("violation")] public string? Violation { init; get; }
    [JsonPropertyName("tieEquivalent")] public bool TieEquivalent { init; get; }
    [JsonPropertyName("rank")] public int Rank { init; get; }
}

/// <summary>某个深度处的不确定度带（由整组样本传播，非均值）。</summary>
public sealed record UncertaintyBandPoint(
    [property: JsonPropertyName("depthM")] double DepthM,
    [property: JsonPropertyName("p05")] double P05,
    [property: JsonPropertyName("p50")] double P50,
    [property: JsonPropertyName("p95")] double P95);

/// <summary>一次完整推导：多候选全部保留（R4），并记录输入摘要/参数版本/父级依赖。</summary>
public sealed class DerivationResult
{
    [JsonPropertyName("derivationId")] public string DerivationId { init; get; } = "";
    [JsonPropertyName("outcome")] public EngineOutcome Outcome { init; get; }
    [JsonPropertyName("parameterVersion")] public string ParameterVersion { init; get; } = "";
    [JsonPropertyName("inputFingerprint")] public string InputFingerprint { init; get; } = "";
    [JsonPropertyName("parentEventId")] public string? ParentEventId { init; get; }
    [JsonPropertyName("changedSourceIds")] public List<string> ChangedSourceIds { init; get; } = new();
    [JsonPropertyName("affectedObjectIds")] public List<string> AffectedObjectIds { init; get; } = new();
    [JsonPropertyName("candidateModels")] public List<CandidateModel> CandidateModels { init; get; } = new();
    [JsonPropertyName("uncertaintyBands")] public Dictionary<string, List<UncertaintyBandPoint>> UncertaintyBands { init; get; } = new();
    [JsonPropertyName("diagnostics")] public List<Diagnostic> Diagnostics { init; get; } = new();
    [JsonPropertyName("selectedModelIds")] public List<string> SelectedModelIds { init; get; } = new();
    [JsonPropertyName("recomputedAt")] public string RecomputedAt { init; get; } = "";
}

/// <summary>可修改的推导参数（参数版本化）。</summary>
public sealed record EngineParameters(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("maxCandidatesPerSegment")] int MaxCandidatesPerSegment = 64,
    [property: JsonPropertyName("tieMisfitToleranceKa")] double TieMisfitToleranceKa = 0.5,
    [property: JsonPropertyName("rateBandGridM")] double RateBandGridM = 0.5,
    [property: JsonPropertyName("requireMonotonic")] bool RequireMonotonic = true);

/// <summary>并列锚点上的一种合法选项（保留其原始锚点身份）。</summary>
internal sealed record AnchorOption(string AnchorId, double DepthM, double[] Samples, string Fingerprint);

/// <summary>
/// 深度-年龄推导引擎（纯服务端逻辑，可直接单元测试）。
/// 规则：R1 年龄仅在不断层段内单调；R2 缺口/不整合为显式边界；
/// R3 锚点分布不被均值替代；R4 等优模型全部保留。
/// </summary>
public sealed class AgeModelEngine
{
    public const string CurrentParameterVersion = "params-1.0.0";
    public const int DefaultMaxOptions = 64;

    private readonly EngineParameters _p;
    private readonly TimeProvider _clock;

    public AgeModelEngine(EngineParameters? parameters = null, TimeProvider? clock = null)
    {
        _p = parameters ?? new EngineParameters(CurrentParameterVersion);
        _clock = clock ?? TimeProvider.System;
    }

    public DerivationResult Derive(
        CoreMaterial material,
        string? parentEventId,
        IReadOnlyCollection<string>? changedSourceIds = null,
        CancellationToken ct = default)
    {
        var diagnostics = new List<Diagnostic>();
        var changed = changedSourceIds ?? new List<string>();
        var outcome = EngineOutcome.Success;
        var candidates = new List<CandidateModel>();
        var bands = new Dictionary<string, List<UncertaintyBandPoint>>();
        var affected = new HashSet<string>();

        ValidateSegments(material, diagnostics);
        var usableBySegment = PrepareAnchors(material, diagnostics, affected);

        var budget = _p.MaxCandidatesPerSegment * Math.Max(1, material.Segments.Count);
        var totalGenerated = 0;

        foreach (var seg in material.Segments)
        {
            ct.ThrowIfCancellationRequested();
            if (!usableBySegment.TryGetValue(seg.Id, out var options) || options.Count == 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                    DiagnosticCodes.IncompleteInput, $"segments[{seg.Id}]", "段内无可用锚点，无法推导年龄模型", seg.Id));
                outcome = outcome == EngineOutcome.Success ? EngineOutcome.DegradedSuccess : outcome;
                bands[seg.Id] = new List<UncertaintyBandPoint>();
                continue;
            }

            // 倒置层段：正常段中年龄随深度变年轻是违例；倒转段允许但标警告。
            var inversions = DetectInversions(seg, options, material);
            foreach (var inv in inversions) diagnostics.Add(inv);
            if (inversions.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                outcome = EngineOutcome.SemanticUnresolvable;
                bands[seg.Id] = new List<UncertaintyBandPoint>();
                continue;
            }

            var combos = EnumerateCombinations(seg, options, diagnostics, ref totalGenerated, budget, ct);
            if (combos == null)
            {
                outcome = EngineOutcome.BudgetExhausted;
                bands[seg.Id] = new List<UncertaintyBandPoint>();
                continue;
            }

            var segCandidates = new List<CandidateModel>();
            foreach (var combo in combos)
            {
                ct.ThrowIfCancellationRequested();
                var model = EvaluateCombo(seg, combo);
                segCandidates.Add(model);
                affected.Add(seg.Id);
                foreach (var a in combo) affected.Add(a.AnchorId);
            }

            RankCandidates(segCandidates, out var tieGroup);
            candidates.AddRange(segCandidates);
            foreach (var m in segCandidates)
                if (tieGroup.Contains(m.ModelId)) m.TieEquivalent = true;

            var best = segCandidates.Where(c => c.Valid).OrderBy(c => c.Rank).FirstOrDefault();
            if (best != null) bands[seg.Id] = BuildBand(seg, options, best, tieGroup);

            if (tieGroup.Count > 1)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, DiagnosticCodes.TiedAnchors,
                    $"segments[{seg.Id}]", $"段 {seg.Label} 存在 {tieGroup.Count} 个等优候选模型，全部保留", seg.Id));
        }

        if (outcome == EngineOutcome.Success && diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            outcome = EngineOutcome.SemanticUnresolvable;
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning) && outcome == EngineOutcome.Success)
            outcome = EngineOutcome.DegradedSuccess;

        var fingerprint = FingerprintMaterial(material);
        return new DerivationResult
        {
            DerivationId = StableId("derive", fingerprint + "|" + _p.Version + "|" + string.Join(",", changed.OrderBy(x => x))),
            Outcome = outcome,
            ParameterVersion = _p.Version,
            InputFingerprint = fingerprint,
            ParentEventId = parentEventId,
            ChangedSourceIds = changed.OrderBy(x => x).ToList(),
            AffectedObjectIds = affected.OrderBy(x => x).ToList(),
            CandidateModels = candidates.OrderBy(c => c.SegmentId).ThenBy(c => c.Rank).ThenBy(c => c.ModelId).ToList(),
            UncertaintyBands = bands,
            Diagnostics = diagnostics,
            SelectedModelIds = candidates.Where(c => c.Rank == 1 && c.Valid).Select(c => c.ModelId).OrderBy(x => x).ToList(),
            RecomputedAt = _clock.GetUtcNow().UtcDateTime.ToString("o"),
        };
    }

    // ---------- 校验与准备 ----------

    private static void ValidateSegments(CoreMaterial m, List<Diagnostic> diags)
    {
        var ordered = m.Segments.OrderBy(s => s.TopM).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (s.BottomM <= s.TopM)
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.SegmentOrder,
                    $"segments[{s.Id}]", $"段 {s.Label} 底界({s.BottomM}) 不深于顶界({s.TopM})", s.Id));
            if (i > 0)
            {
                var prev = ordered[i - 1];
                // 显式缺口：prev.BottomM &lt; s.TopM 是合法的 hiatus/gap；重叠才是错误。
                if (s.TopM < prev.BottomM - 1e-9)
                    diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.SegmentOverlap,
                        $"segments[{prev.Id},{s.Id}]", $"段 {prev.Label} 与 {s.Label} 在深度上重叠", s.Id));
            }
            if (s.Overturned)
                diags.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.Overturned,
                    $"segments[{s.Id}]", $"段 {s.Label} 标记为倒转层段，允许局部年龄倒置", s.Id));
        }
    }

    private Dictionary<string, List<AnchorOption>> PrepareAnchors(
        CoreMaterial m, List<Diagnostic> diags, HashSet<string> affected)
    {
        var result = new Dictionary<string, List<AnchorOption>>();
        var reservoirById = m.ReservoirCorrections.ToDictionary(r => r.Id);

        foreach (var anchor in m.Anchors)
        {
            var path = $"anchors[{anchor.Id}]";
            if (anchor.Excluded)
            {
                diags.Add(new Diagnostic(DiagnosticSeverity.Info, DiagnosticCodes.ExcludedAnchor,
                    path, $"锚点按排除决定跳过：{anchor.ExclusionReason}", anchor.Id));
                continue;
            }
            if (!anchor.DepthM.HasValue)
            {
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.UnconvertibleDepth,
                    path, $"深度无法规范化，原始值保留在原位：'{anchor.DepthRaw}'", anchor.Id));
                continue;
            }
            if (anchor.AgeDistribution is null || anchor.AgeDistribution.Samples.Length == 0)
            {
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.IncompleteInput,
                    path, "缺少年龄分布（禁止以均值替代）", anchor.Id));
                continue;
            }
            var seg = m.Segments.FirstOrDefault(s => s.Id == anchor.SegmentId);
            if (seg is null)
            {
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.AnchorOutsideSegment,
                    path, "锚点不属于任何已知段", anchor.Id));
                continue;
            }
            if (anchor.DepthM.Value < seg.TopM - 1e-9 || anchor.DepthM.Value > seg.BottomM + 1e-9)
            {
                diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.AnchorOutsideSegment,
                    path, $"锚点深度 {anchor.DepthM} m 超出段 {seg.Label} 边界 [{seg.TopM},{seg.BottomM}]", anchor.Id));
                continue;
            }
            if (!string.IsNullOrEmpty(anchor.ReservoirCorrectionId))
            {
                if (!reservoirById.TryGetValue(anchor.ReservoirCorrectionId, out var rc))
                {
                    diags.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.MissingReservoir,
                        path, "引用的储层偏移不存在，按未校正处理", anchor.Id));
                }
                else
                {
                    var corr = AgeDistribution.Normal(-rc.MeanKa, rc.SigmaKa, rc.Seed + 999, null,
                        anchor.AgeDistribution.Samples.Length);
                    var shifted = anchor.AgeDistribution.Samples.Zip(corr.Samples).Select(t => t.First + t.Second).ToArray();
                    Array.Sort(shifted);
                    var corrected = AgeDistribution.Empirical(shifted, anchor.AgeDistribution.RawDescription + $" + ΔR({rc.Id})");
                    SetDistribution(anchor, corrected);
                }
            }
            if (anchor.AgeDistribution.LongTail)
                diags.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.LongTail,
                    path, $"锚点年龄分布存在长尾（保留完整分布，均值 {anchor.AgeDistribution.Mean:0.##} ka 仅作展示）", anchor.Id));

            var fp = StableId("opt", anchor.Id + "@" + anchor.DepthM.Value.ToString("G") + ":" +
                (anchor.AgeDistribution.Samples.Length) + ":" +
                anchor.AgeDistribution.Samples[0].ToString("G") + ":" +
                anchor.AgeDistribution.Samples[^1].ToString("G"));
            result.GetOrAdd(anchor.SegmentId).Add(new AnchorOption(anchor.Id, anchor.DepthM.Value,
                anchor.AgeDistribution.Samples, fp));
            affected.Add(anchor.Id);
        }

        // 深度重复：同深度多锚点是并列选项而非错误，记录诊断并交枚举器展开（R4 相关情形）。
        foreach (var kv in result)
        {
            var dups = kv.Value.GroupBy(o => o.DepthM).Where(g => g.Count() > 1).ToList();
            foreach (var g in dups)
                diags.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.DuplicateDepth,
                    $"segments[{kv.Key}]", $"深度 {g.Key:0.##} m 上有 {g.Count()} 个并列锚点：{string.Join(",", g.Select(x => x.AnchorId))}"));
        }
        return result;
    }

    private static void SetDistribution(AgeAnchor anchor, AgeDistribution dist)
    {
        // AgeAnchor 用 init 属性；用反射无关的重写通过克隆完成（锚点可变对象仅限引擎内部规范化）。
        typeof(AgeAnchor).GetProperty(nameof(AgeAnchor.AgeDistribution))!.SetValue(anchor, dist);
    }

    private List<Diagnostic> DetectInversions(Segment seg, List<AnchorOption> options, CoreMaterial m)
    {
        var diags = new List<Diagnostic>();
        var byDepth = options.OrderBy(o => o.DepthM).ToList();
        for (var i = 1; i < byDepth.Count; i++)
        {
            var shallower = byDepth[i - 1];
            var deeper = byDepth[i];
            // 同深度（并列锚点）不算倒置。
            if (Math.Abs(shallower.DepthM - deeper.DepthM) < 1e-9) continue;
            var shallowerMed = Median(shallower.Samples);
            var deeperMed = Median(deeper.Samples);
            if (deeperMed + _p.TieMisfitToleranceKa &lt; shallowerMed)
            {
                if (seg.Overturned)
                    diags.Add(new Diagnostic(DiagnosticSeverity.Warning, DiagnosticCodes.Inversion,
                        $"segments[{seg.Id}]", $"倒转段 {seg.Label} 内 {shallower.AnchorId}→{deeper.AnchorId} 年龄变年轻（允许）", deeper.AnchorId));
                else
                    diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.MonotonicityViolation,
                        $"segments[{seg.Id}]", $"段 {seg.Label} 内年龄不随深度变老：{shallower.AnchorId}({shallowerMed:0.##}) &gt; {deeper.AnchorId}({deeperMed:0.##})", deeper.AnchorId));
            }
        }
        return diags;
    }

    // ---------- 组合枚举（并列锚点展开，预算耗尽为独立结果） ----------

    private List<List<AnchorOption>>? EnumerateCombinations(
        Segment seg, List<AnchorOption> options, List<Diagnostic> diags,
        ref int totalGenerated, int budget, CancellationToken ct)
    {
        var clusters = options
            .GroupBy(o => Math.Round(o.DepthM, 6))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(o => o.Fingerprint).ToList())
            .ToList();

        var combos = new List<List<AnchorOption>> { new() };
        foreach (var cluster in clusters)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<List<AnchorOption>>(combos.Count * cluster.Count);
            foreach (var existing in combos)
            foreach (var choice in cluster)
            {
                next.Add(new List<AnchorOption>(existing) { choice });
                totalGenerated++;
                if (totalGenerated > budget)
                {
                    diags.Add(new Diagnostic(DiagnosticSeverity.Error, DiagnosticCodes.BudgetExhausted,
                        $"segments[{seg.Id}]",
                        $"候选组合数超过预算 {budget}（并列锚点过多），停止展开以保留有界计算", seg.Id));
                    return null;
                }
            }
            combos = next;
        }
        // 稳定顺序：等价输入得到稳定顺序
        return combos
            .OrderBy(c => c.Count)
            .ThenBy(c => string.Join("|", c.Select(o => o.Fingerprint)))
            .ToList();
    }

    private CandidateModel EvaluateCombo(Segment seg, List<AnchorOption> combo)
    {
        var ordered = combo.OrderBy(o => o.DepthM).ToList();
        var fps = ordered.Select(o => o.Fingerprint).OrderBy(x => x).ToList();
        var modelId = StableId("model", seg.Id + "|" + string.Join("|", fps));

        if (ordered.Select(o => o.DepthM).Distinct().Count() < 2)
        {
            return new CandidateModel
            {
                ModelId = modelId, SegmentId = seg.Id,
                AnchorIds = ordered.Select(o => o.AnchorId).ToList(),
                OptionFingerprints = fps, Valid = false,
                Violation = "INSUFFICIENT_ANCHORS: 少于两个不同深度，无法确定速率",
                RateKaperM = double.NaN, RateP05 = double.NaN, RateP95 = double.NaN, Misfit = double.PositiveInfinity,
            };
        }

        var n = ordered[0].Samples.Length;
        var depths = ordered.Select(o => o.DepthM).ToArray();
        var meds = ordered.Select(o => Median(o.Samples)).ToArray();
        var slope = LeastSquaresSlope(depths, meds);
        var intercept = meds.Average() - slope * depths.Average();
        var misfit = ordered.Select((o, i) =>
            Math.Abs(meds[i] - (slope * depths[i] + intercept))).Average();

        // 分布传播：逐样本拟合斜率，得到速率分布（不使用均值近似）。
        var slopeSamples = new double[n];
        var monotone = true;
        for (var j = 0; j < n; j++)
        {
            var ages = ordered.Select(o => o.Samples[Math.Min(j, o.Samples.Length - 1)]).ToArray();
            slopeSamples[j] = LeastSquaresSlope(depths, ages);
        }
        Array.Sort(slopeSamples);
        if (_p.RequireMonotonic && !seg.Overturned)
        {
            for (var i = 1; i < ordered.Count; i++)
            {
                if (Math.Abs(depths[i] - depths[i - 1]) < 1e-9) continue;
                var deltaMed = meds[i] - meds[i - 1];
                if (deltaMed &lt; -_p.TieMisfitToleranceKa) { monotone = false; break; }
            }
        }

        return new CandidateModel
        {
            ModelId = modelId, SegmentId = seg.Id,
            AnchorIds = ordered.Select(o => o.AnchorId).ToList(),
            OptionFingerprints = fps,
            RateKaperM = slope,
            RateP05 = QuantileSorted(slopeSamples, 0.05),
            RateP95 = QuantileSorted(slopeSamples, 0.95),
            Misfit = misfit,
            Valid = monotone,
            Violation = monotone ? null : "MONOTONICITY: 段内组合违反年龄单调（仅允许在显式倒转段内）",
        };
    }

    private static void RankCandidates(List<CandidateModel> segCandidates, out HashSet<string> tieGroup)
    {
        var valid = segCandidates.Where(c => c.Valid).OrderBy(c => c.Misfit).ThenBy(c => c.ModelId).ToList();
        var invalid = segCandidates.Where(c => !c.Valid).ToList();
        var rank = 1;
        double? lastMisfit = null;
        string? lastId = null;
        foreach (var c in valid)
        {
            if (lastMisfit is not null && !(c.Misfit &lt;= lastMisfit.Value + 1e-6)) rank++;
            c.Rank = rank;
            lastMisfit = c.Misfit;
            lastId = c.ModelId;
        }
        foreach (var c in invalid) c.Rank = int.MaxValue;
        var bestMisfit = valid.FirstOrDefault()?.Misfit;
        tieGroup = new HashSet<string>(
            valid.Where(c => bestMisfit is not null && Math.Abs(c.Misfit - bestMisfit.Value) &lt;= 0.5)
                 .Select(c => c.ModelId));
    }

    private List<UncertaintyBandPoint> BuildBand(
        Segment seg, List<AnchorOption> allOptions, CandidateModel best, HashSet<string> tieGroup)
    {
        var chosenIds = new HashSet<string>(best.AnchorIds);
        var pts = allOptions.Where(o => chosenIds.Contains(o.AnchorId))
            .OrderBy(o => o.DepthM).ToList();
        var points = new List<UncertaintyBandPoint>();
        for (var d = seg.TopM; d &lt;= seg.BottomM + 1e-9; d += _p.RateBandGridM)
        {
            var depth = Math.Round(d, 4);
            var ages = InterpolateAgeSamples(pts, depth, best.RateKaperM);
            if (ages is null) { points.Add(new UncertaintyBandPoint(depth, double.NaN, double.NaN, double.NaN)); continue; }
            Array.Sort(ages);
            points.Add(new UncertaintyBandPoint(depth,
                QuantileSorted(ages, 0.05), QuantileSorted(ages, 0.5), QuantileSorted(ages, 0.95)));
        }
        return points;
    }

    private static double[]? InterpolateAgeSamples(List<AnchorOption> pts, double depth, double edgeRate)
    {
        if (pts.Count == 0) return null;
        var n = pts[0].Samples.Length;
        var outp = new double[n];
        var first = pts[0];
        var last = pts[^1];
        for (var j = 0; j < n; j++)
        {
            if (depth &lt;= first.DepthM)
                outp[j] = first.Samples[Math.Min(j, first.Samples.Length - 1)] - edgeRate * (first.DepthM - depth);
            else if (depth >= last.DepthM)
                outp[j] = last.Samples[Math.Min(j, last.Samples.Length - 1)] + edgeRate * (depth - last.DepthM);
            else
            {
                for (var k = 1; k &lt; pts.Count; k++)
                {
                    if (depth &lt;= pts[k].DepthM)
                    {
                        var a = pts[k - 1]; var b = pts[k];
                        var t = (depth - a.DepthM) / (b.DepthM - a.DepthM);
                        outp[j] = a.Samples[Math.Min(j, a.Samples.Length - 1)] * (1 - t)
                                  + b.Samples[Math.Min(j, b.Samples.Length - 1)] * t;
                        break;
                    }
                }
            }
        }
        return outp;
    }

    // ---------- 工具 ----------

    private static double Median(double[] sorted) => QuantileSorted(sorted, 0.5);

    private static double QuantileSorted(double[] s, double p)
        => s[Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1)];

    private static double LeastSquaresSlope(double[] x, double[] y)
    {
        var mx = x.Average(); var my = y.Average();
        var num = x.Zip(y).Sum(t => (t.First - mx) * (t.Second - my));
        var den = x.Sum(v => (v - mx) * (v - mx));
        return den &lt;= 1e-12 ? double.NaN : num / den;
    }

    public static string FingerprintMaterial(CoreMaterial material)
    {
        var json = JsonSerializer.Serialize(material);
        return StableId("material", json);
    }

    public static string StableId(string prefix, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return prefix + "_" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
