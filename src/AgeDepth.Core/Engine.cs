using System.Globalization;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

public sealed record ParamSet(int McDraws, int MaxCandidates, double MonotonicThreshold)
{
    public static readonly ParamSet V1 = new(256, 64, 0.5);
    public static readonly ParamSet V2 = new(512, 96, 0.5);

    public JsonNode ToJson() => new JsonObject
    {
        ["mcDraws"] = McDraws,
        ["maxCandidates"] = MaxCandidates,
        ["monotonicThreshold"] = MonotonicThreshold,
    };
}

internal sealed record ParsedAnchor(
    Material Material,
    string DepthMId,
    double DepthM,
    double[] AgeSamplesKa,
    string SourceKind,
    string? LabCode);

internal sealed record ParsedBoundary(double DepthM, string MaterialId, string Kind, double? EndDepthM);

internal sealed record ParsedOffset(double FromM, double ToM, double ShiftM);

/// <summary>
/// 纯函数推导：不接触数据库与 HTTP，可被测试直接调用。
/// 规则：年龄只允许在不断层段内单调；深度缺口/不整合是显式边界；
/// 锚点使用完整年龄分布，绝不以均值替代；等优候选全部保留。
/// </summary>
public static class Engine
{
    public static DeriveResult Derive(
        IReadOnlyList<Material> materials,
        IReadOnlyList<Decision> decisions,
        ParamSet param,
        int paramVersion)
    {
        var diagnostics = new List<Diagnostic>();
        var offsets = new List<ParsedOffset>();
        var boundaries = new List<ParsedBoundary>();
        var anchors = new List<ParsedAnchor>();
        var excluded = new HashSet<string>();
        var benchmarkDepths = new List<(double d, Material m)>();

        foreach (var mat in materials.Where(m => m.SupersededBy is null))
        {
            switch (mat.Kind)
            {
                case MaterialKind.Exclusion:
                {
                    var target = mat.Payload["targetMaterialId"]?.GetValue<string>();
                    if (target is not null) excluded.Add(target);
                    break;
                }
                case MaterialKind.DepthBenchmark:
                {
                    var q = ReadQuantity(mat.Payload["depth"], $"materials[{mat.Ord}].depth", diagnostics, mat.Id);
                    if (q.HasValue) benchmarkDepths.Add((q.Value, mat));
                    break;
                }
                case MaterialKind.SegmentGap:
                {
                    var from = ReadQuantity(mat.Payload["fromDepth"], $"materials[{mat.Ord}].fromDepth", diagnostics, mat.Id);
                    var to = ReadQuantity(mat.Payload["toDepth"], $"materials[{mat.Ord}].toDepth", diagnostics, mat.Id);
                    if (from.HasValue && to.HasValue)
                    {
                        boundaries.Add(new ParsedBoundary(from.Value, mat.Id, "gap", to.Value));
                        boundaries.Add(new ParsedBoundary(to.Value, mat.Id, "gap-end", from.Value));
                    }
                    break;
                }
                case MaterialKind.Unconformity:
                {
                    var at = ReadQuantity(mat.Payload["atDepth"], $"materials[{mat.Ord}].atDepth", diagnostics, mat.Id);
                    if (at.HasValue) boundaries.Add(new ParsedBoundary(at.Value, mat.Id, "unconformity", null));
                    break;
                }
                case MaterialKind.ReservoirOffset:
                {
                    var from = ReadQuantity(mat.Payload["fromDepth"], $"materials[{mat.Ord}].fromDepth", diagnostics, mat.Id);
                    var to = ReadQuantity(mat.Payload["toDepth"], $"materials[{mat.Ord}].toDepth", diagnostics, mat.Id);
                    var shift = ReadQuantity(mat.Payload["shift"], $"materials[{mat.Ord}].shift", diagnostics, mat.Id);
                    if (from.HasValue && to.HasValue && shift.HasValue)
                        offsets.Add(new ParsedOffset(from.Value, to.Value, shift.Value));
                    break;
                }
                case MaterialKind.RadiometricAge:
                case MaterialKind.EventLayer:
                {
                    var depth = ReadQuantity(mat.Payload["depth"], $"materials[{mat.Ord}].depth", diagnostics, mat.Id);
                    var samples = ReadAgeSamples(mat.Payload["age"], $"materials[{mat.Ord}].age", diagnostics, mat.Id);
                    if (!depth.HasValue || samples is null)
                    {
                        diagnostics.Add(new Diagnostic("material.unusable",
                            "材料保留在档案中，但因缺深度或年龄分布而不参与推导",
                            $"materials[{mat.Ord}]", mat.Id));
                        break;
                    }
                    anchors.Add(new ParsedAnchor(mat, mat.Id, depth.Value, samples, mat.Kind,
                        mat.Payload["labCode"]?.GetValue<string>()));
                    break;
                }
            }
        }

        foreach (var dup in benchmarkDepths.GroupBy(x => x.d).Where(g => g.Count() > 1))
        {
            diagnostics.Add(new Diagnostic("depth.duplicate",
                $"深度基准在 {dup.Key.ToString(CultureInfo.InvariantCulture)} m 重复（{dup.Count()} 条，均保留）",
                "depth-benchmarks", string.Join(",", dup.Select(x => x.m.Id))));
        }

        // 储层偏移只在派生层应用，原始深度保持不变。
        for (var i = 0; i < anchors.Count; i++)
        {
            var a = anchors[i];
            var shift = offsets.Where(o => a.DepthM >= o.FromM && a.DepthM <= o.ToM).Sum(o => o.ShiftM);
            if (shift != 0) anchors[i] = a with { DepthM = a.DepthM + shift };
        }

        var extents = anchors.Select(a => a.DepthM)
            .Concat(boundaries.Select(b => b.DepthM))
            .Concat(benchmarkDepths.Select(b => b.d)).ToList();
        if (extents.Count == 0)
        {
            diagnostics.Add(new Diagnostic("semantic.unsolvable",
                "没有任何可用深度或年龄材料，无法建立年龄模型", "$", null));
            return new DeriveResult.Unsolvable(diagnostics);
        }

        var segments = BuildSegments(boundaries, extents.Min(), extents.Max());
        var allCandidates = new List<Candidate>();
        var unsolvable = new List<Diagnostic>();

        foreach (var seg in segments)
        {
            var inSeg = anchors
                .Where(a => a.DepthM >= seg.TopM - 1e-9 && a.DepthM <= seg.BottomM + 1e-9 &&
                            !excluded.Contains(a.Material.Id))
                .OrderBy(a => a.DepthM)
                .ToList();

            if (inSeg.Count == 0)
            {
                unsolvable.Add(new Diagnostic("semantic.unsolvable",
                    $"层段 {seg.Id}（{seg.TopM}–{seg.BottomM} m）内没有锚点，无法唯一决定年龄模型",
                    "segments", seg.Id));
                continue;
            }

            var groups = inSeg.GroupBy(a => a.DepthM).OrderBy(g => g.Key).ToList();
            foreach (var g in groups.Where(g => g.Count() > 1))
            {
                diagnostics.Add(new Diagnostic("anchor.tied",
                    $"深度 {g.Key.ToString(CultureInfo.InvariantCulture)} m 存在 {g.Count()} 个并列锚点，拆分为多个候选",
                    "segments." + seg.Id, string.Join(",", g.Select(x => x.Material.Id))));
            }

            var combos = Cartesian(groups.Select(g => g.ToList()).ToList());
            if (combos.Count > param.MaxCandidates)
                return new DeriveResult.BudgetExhausted(combos.Count, param.MaxCandidates);

            foreach (var combo in combos)
            {
                var ordered = combo.OrderBy(a => a.DepthM).ToList();
                var anchorIds = ordered.Select(a => a.Material.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var inversion = FindHardInversion(ordered);
                var id = StableId.For("cand", StableId.Canonical(new JsonObject
                {
                    ["segment"] = seg.Id,
                    ["anchors"] = new JsonArray(anchorIds.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                    ["param"] = paramVersion,
                }));

                if (inversion is not null)
                {
                    allCandidates.Add(new Candidate(id, seg.Id, anchorIds, [], [],
                        0, false, CandidateStatus.Rejected, 1, id,
                        $"倒置层段：{inversion.Shallow.Material.Id}({inversion.Shallow.DepthM}m) 与 " +
                        $"{inversion.Deep.Material.Id}({inversion.Deep.DepthM}m) 的年龄分布不支持单调"));
                    continue;
                }

                var (rates, band, support) = MonteCarlo(seg, ordered, param, id);
                var status = support < param.MonotonicThreshold
                    ? CandidateStatus.Rejected
                    : CandidateStatus.Active;
                allCandidates.Add(new Candidate(id, seg.Id, anchorIds, rates, band,
                    support, false, status, 1, id,
                    status == CandidateStatus.Rejected
                        ? $"单调支持率 {support:F2} 低于阈值 {param.MonotonicThreshold}" : null));
            }
        }

        if (unsolvable.Count > 0 && !allCandidates.Any(c => c.Status == CandidateStatus.Active))
            return new DeriveResult.Unsolvable(unsolvable);
        diagnostics.AddRange(unsolvable);

        MarkEqualRank(allCandidates);
        var orderedCandidates = allCandidates
            .OrderBy(c => c.SegmentId, StringComparer.Ordinal)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();
        return new DeriveResult.Ok(new DerivedState(segments.ToArray(), orderedCandidates,
            diagnostics.OrderBy(d => d.Path, StringComparer.Ordinal).ToArray(), paramVersion));
    }

    private static List<Segment> BuildSegments(List<ParsedBoundary> boundaries, double min, double max)
    {
        var cuts = boundaries
            .Where(b => b.Kind is "gap-end" || b.Kind == "unconformity")
            .Select(b => b.DepthM)
            .Where(d => d > min && d < max)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        var result = new List<Segment>();
        var top = min;
        var topBound = "core-top";
        var gaps = boundaries.Where(b => b.Kind == "gap").ToList();
        var i = 0;
        foreach (var cut in cuts)
        {
            var b = boundaries.First(x => Math.Abs(x.DepthM - cut) < 1e-9);
            var label = b.Kind == "gap-end"
                ? $"gap:{gaps.First(g => Math.Abs(g.DepthM - (b.EndDepthM ?? 0)) < 1e-9).MaterialId}"
                : $"unconformity:{b.MaterialId}";
            result.Add(new Segment($"seg-{i + 1}", top, cut, topBound, label));
            top = cut;
            topBound = label;
            i++;
        }
        result.Add(new Segment($"seg-{i + 1}", top, max, topBound, "core-bottom"));
        return result;
    }

    private static List<List<ParsedAnchor>> Cartesian(List<List<ParsedAnchor>> groups)
    {
        var result = new List<List<ParsedAnchor>> { new() };
        foreach (var group in groups)
        {
            var next = new List<List<ParsedAnchor>>();
            foreach (var prefix in result)
            foreach (var choice in group)
                next.Add([.. prefix, choice]);
            result = next;
        }
        return result;
    }

    private static (ParsedAnchor Shallow, ParsedAnchor Deep)? FindHardInversion(List<ParsedAnchor> ordered)
    {
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var (shallow, deep) = (ordered[i], ordered[i + 1]);
            if (Quantile(shallow.AgeSamplesKa, 0.95) < Quantile(deep.AgeSamplesKa, 0.05))
                return (shallow, deep);
        }
        return null;
    }

    private static void MarkEqualRank(List<Candidate> candidates)
    {
        foreach (var grp in candidates.Where(c => c.Status == CandidateStatus.Active)
                     .GroupBy(c => c.SegmentId))
        {
            var best = grp.Max(c => c.Score);
            var top = grp.Where(c => Math.Abs(c.Score - best) < 1e-9).ToList();
            if (top.Count > 1)
                foreach (var c in top)
                {
                    var idx = candidates.FindIndex(x => x.Id == c.Id);
                    candidates[idx] = c with { EqualRank = true };
                }
        }
    }

    private static (RateInterval[] Rates, BandNode[] Band, double Support) MonteCarlo(
        Segment seg, List<ParsedAnchor> anchors, ParamSet param, string candidateId)
    {
        var seed = int.Parse(candidateId[^8..], System.Globalization.NumberStyles.HexNumber);
        var rng = new Random(seed);
        var n = anchors.Count;
        var draws = new double[param.McDraws][];
        var monotonic = 0;
        for (var d = 0; d < param.McDraws; d++)
        {
            var row = new double[n];
            var mono = true;
            for (var i = 0; i < n; i++)
            {
                var s = anchors[i].AgeSamplesKa;
                row[i] = s[rng.Next(s.Length)];
                if (i > 0 && row[i] < row[i - 1]) mono = false;
            }
            if (mono) monotonic++;
            draws[d] = row;
        }
        var support = (double)monotonic / param.McDraws;

        var evalDepths = new HashSet<double> { seg.TopM, seg.BottomM };
        foreach (var a in anchors) evalDepths.Add(a.DepthM);
        var sortedDepths = evalDepths.OrderBy(x => x).ToArray();
        var bands = new List<BandNode>();
        foreach (var depth in sortedDepths)
        {
            var vals = new List<double>();
            for (var d = 0; d < param.McDraws; d++)
                vals.Add(Interpolate(draws[d], anchors, depth));
            bands.Add(new BandNode(depth, Quantile(vals, 0.05), Quantile(vals, 0.5), Quantile(vals, 0.95)));
        }

        var rates = new List<RateInterval>();
        for (var i = 0; i < n - 1; i++)
        {
            var vals = new List<double>();
            for (var d = 0; d < param.McDraws; d++)
            {
                var ageGap = draws[d][i + 1] - draws[d][i];
                if (ageGap > 1e-9)
                    vals.Add((anchors[i + 1].DepthM - anchors[i].DepthM) * 100.0 / ageGap);
            }
            if (vals.Count > 0)
                rates.Add(new RateInterval(anchors[i].DepthM, anchors[i + 1].DepthM,
                    Quantile(vals, 0.05), Quantile(vals, 0.5), Quantile(vals, 0.95)));
        }
        return (rates.ToArray(), bands.ToArray(), support);
    }

    private static double Interpolate(double[] ages, List<ParsedAnchor> anchors, double depth)
    {
        var ds = anchors.Select(a => a.DepthM).ToList();
        if (depth <= ds[0])
        {
            if (ds.Count == 1) return ages[0];
            var slope = (ages[1] - ages[0]) / (ds[1] - ds[0]);
            return ages[0] + slope * (depth - ds[0]);
        }
        if (depth >= ds[^1])
        {
            if (ds.Count == 1) return ages[^1];
            var slope = (ages[^1] - ages[^2]) / (ds[^1] - ds[^2]);
            return ages[^1] + slope * (depth - ds[^1]);
        }
        for (var i = 0; i < ds.Count - 1; i++)
        {
            if (depth >= ds[i] && depth <= ds[i + 1])
            {
                var t = (depth - ds[i]) / (ds[i + 1] - ds[i]);
                return ages[i] + t * (ages[i + 1] - ages[i]);
            }
        }
        return ages[^1];
    }

    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        var pos = (sorted.Length - 1) * q;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static double? ReadQuantity(JsonNode? node, string path, List<Diagnostic> diags, string? materialId)
    {
        if (node is null)
        {
            diags.Add(new Diagnostic("quantity.missing", "物理量缺失，不以默认值伪装", path, materialId));
            return null;
        }
        var raw = node["raw"]?.GetValue<string>() ?? node["value"]?.ToString();
        var unit = node["unit"]?.GetValue<string>() ?? "";
        if (!double.TryParse(node["value"]?.ToString(), System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value))
        {
            diags.Add(new Diagnostic("quantity.unparseable", $"无法转换的值保留原位：{raw}", path, materialId));
            return null;
        }
        var factor = unit switch
        {
            "m" => 1.0,
            "cm" => 0.01,
            "mm" => 0.001,
            "ka BP" => 1.0,
            "ma BP" or "Ma BP" => 1000.0,
            _ => double.NaN,
        };
        if (double.IsNaN(factor))
        {
            diags.Add(new Diagnostic("unit.unconvertible", $"未知单位 '{unit}'，原值保留：{raw}", path, materialId));
            return null;
        }
        return value * factor;
    }

    private static double[]? ReadAgeSamples(JsonNode? node, string path, List<Diagnostic> diags, string? materialId)
    {
        if (node?["samples"] is not JsonArray arr || arr.Count == 0)
        {
            diags.Add(new Diagnostic("age.distribution-missing",
                "放射性年龄必须提供完整分布（samples），不接受单一均值", path, materialId));
            return null;
        }
        var samples = new List<double>();
        foreach (var v in arr)
            if (double.TryParse(v?.ToString(), System.Globalization.NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var x))
                samples.Add(x);
        if (samples.Count == 0)
        {
            diags.Add(new Diagnostic("age.distribution-empty", "年龄分布样本全部无法解析", path, materialId));
            return null;
        }
        return samples.ToArray();
    }
}
