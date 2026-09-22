using ChronoCore.Domain;

namespace ChronoCore.Engine;

public sealed partial class AgeModelEngine
{
    /// <summary>
    /// 构建有效锚点。放射性年龄保留经验分布（含伽马上尾）；
    /// 并列锚点（同深不同源）各自保留，绝不均值合并；重复深度（同深同源）报诊断但材料保留。
    /// </summary>
    internal List<EffectiveAnchor> BuildAnchors(
        IReadOnlyList<Material> active, List<SegmentCtx> segments,
        ParameterSet p, List<DiagnosticEntry> diag)
    {
        var reservoirs = active.Where(m => m.Kind == MaterialKind.ReservoirOffset).ToList();
        var candidates = new List<(Material Mat, double Depth, bool IsEvent)>();

        foreach (var m in active)
        {
            if (m.Excluded)
            {
                diag.Add(new DiagnosticEntry { Code = "I-EXCLUDED", Severity = Severity.Info, Location = $"{m.SourceRef}:{m.SourceLine}", MaterialId = m.Id, Message = $"材料已被排除决定保留在档：{m.ExclusionReason}" });
                continue;
            }
            if (m.Kind == MaterialKind.RadiometricDate && m.DepthRaw?.ValueSi is double d && m.AgeRaw?.ValueSi is not null)
                candidates.Add((m, d, false));
            if (m.Kind == MaterialKind.EventCorrelation && m.EventDepthRaw?.ValueSi is double ed && m.EventAgeRaw?.ValueSi is not null)
                candidates.Add((m, ed, true));
        }

        // 深度重复 / 并列锚点检测
        foreach (var grp in candidates.GroupBy(c => Math.Round(c.Depth, 4)))
        {
            if (grp.Count() < 2) continue;
            var sameSource = grp.Select(c => c.Mat.SourceRef).Distinct(StringComparer.Ordinal).Count() == 1;
            foreach (var c in grp)
                diag.Add(new DiagnosticEntry
                {
                    Code = sameSource ? "W-DUP-DEPTH" : "W-TIED-ANCHOR",
                    Severity = sameSource ? Severity.Warning : Severity.Info,
                    Location = $"{c.Mat.SourceRef}:{c.Mat.SourceLine}",
                    MaterialId = c.Mat.Id,
                    Message = sameSource
                        ? $"深度重复：{c.Depth:F3} mbsf 处出现同源多条记录，材料保留，需人工裁决是否录入错误"
                        : $"并列锚点：{c.Depth:F3} mbsf 处存在 {grp.Count()} 个独立来源锚点，各自保留分布，禁止均值合并"
                });
        }

        var anchors = new List<EffectiveAnchor>();
        foreach (var (mat, depth, isEvent) in candidates.OrderBy(c => c.Depth).ThenBy(c => c.Mat.Id, StringComparer.Ordinal))
        {
            var seg = LocateSegment(segments, depth);
            var anchor = new EffectiveAnchor
            {
                AnchorId = "anc_" + mat.Id,
                SourceMaterialId = mat.Id,
                SourceRef = mat.SourceRef,
                DepthMbsf = depth,
                SegmentId = seg?.SegmentId ?? "seg_UNBOUNDED",
                IsEventAnchor = isEvent
            };

            double mu, sigma;
            bool longTail = false;
            double tailMass = 0;
            if (isEvent)
            {
                mu = mat.EventAgeRaw!.ValueSi!.Value;
                sigma = Math.Abs(mat.EventAgeSigmaRaw?.ValueSi ?? 0);
            }
            else
            {
                mu = mat.AgeRaw!.ValueSi!.Value;
                sigma = Math.Abs(mat.OneSigmaRaw?.ValueSi ?? 0);

                // 储层偏移：仅在有效区间内对海相材料扣除；区间外原位保留并给失效原因
                if (p.ApplyReservoirToMarine && string.Equals(mat.MaterialType, "marine", StringComparison.OrdinalIgnoreCase)
                    && mat.ReservoirId is not null)
                {
                    var res = reservoirs.FirstOrDefault(r => r.Id == mat.ReservoirId);
                    if (res?.DeltaR?.ValueSi is double dr)
                    {
                        var from = res.ValidFromAge?.ValueSi ?? double.NegativeInfinity;
                        var to = res.ValidToAge?.ValueSi ?? double.PositiveInfinity;
                        if (mu >= from && mu <= to) mu -= dr;
                        else
                        {
                            anchor.Note = $"储层偏移 {res.ReservoirName} 有效区间 [{from},{to}] cal yr BP 外，未施加；原始年龄 {mat.AgeRaw.Raw} 保留";
                            diag.Add(new DiagnosticEntry { Code = "W-RESERVOIR-RANGE", Severity = Severity.Warning, Location = $"{mat.SourceRef}:{mat.SourceLine}", MaterialId = mat.Id, Message = anchor.Note });
                        }
                    }
                }

                longTail = string.Equals(mat.Distribution, "gamma-longtail", StringComparison.OrdinalIgnoreCase);
                if (longTail)
                {
                    var draws = DrawGammaMixture(mat, mu, sigma, p.MonteCarloDraws, out tailMass);
                    _drawsUsed += draws.Length;
                    FillQuantiles(anchor, draws);
                    anchor.LongTail = true;
                    anchor.TailMass = tailMass;
                }
            }

            if (!longTail)
            {
                var draws = DrawNormal(mu, sigma, p.MonteCarloDraws, StableSeed(mat.Id + "|normal"));
                _drawsUsed += draws.Length;
                FillQuantiles(anchor, draws);
            }
            if (seg is null)
            {
                anchor.Note = "锚点深度落在所有已声明分段之外（缺口另一侧/未覆盖区间），不参与任何段内插值";
                diag.Add(new DiagnosticEntry { Code = "W-ANCHOR-UNBOUNDED", Severity = Severity.Warning, Location = $"{mat.SourceRef}:{mat.SourceLine}", MaterialId = mat.Id, Message = anchor.Note });
            }
            else if (seg.Overturned)
            {
                anchor.Note = "锚点位于倒置层段：该段不声明单调年龄，锚点只作展示证据";
                diag.Add(new DiagnosticEntry { Code = "E-INVERTED", Severity = Severity.Error, Location = seg.SegmentId, MaterialId = mat.Id, Message = $"倒置层段 {seg.SegmentId} 内的年龄只能单独展示，不能纳入单调深度-年龄模型" });
            }
            else
            {
                seg.AnchorCount++;
                if (_clock.ElapsedTicks > p.CpuBudgetTicks / 2) _budgetHit = true;
            }
            if (mat.ExclusionReason is not null) anchor.ExclusionReason = mat.ExclusionReason;
            anchors.Add(anchor);
        }
        return anchors;
    }

    private static SegmentCtx? LocateSegment(List<SegmentCtx> segments, double depth) =>
        segments.FirstOrDefault(s => depth >= s.Top - 1e-9 && depth <= s.Base + 1e-9);

    private static void FillQuantiles(EffectiveAnchor a, double[] draws)
    {
        var sorted = draws.Order().ToArray();
        a.Q025 = Quantile(sorted, .025); a.Q16 = Quantile(sorted, .16);
        a.MeanCalYrBp = sorted[(int)(sorted.Length * 0.5)];
        a.Q84 = Quantile(sorted, .84); a.Q975 = Quantile(sorted, .975);
    }

    internal static double Quantile(double[] sortedAsc, double q)
    {
        var idx = Math.Clamp((int)Math.Round(q * (sortedAsc.Length - 1)), 0, sortedAsc.Length - 1);
        return sortedAsc[idx];
    }

    private static int StableSeed(string key)
    {
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt32(h, 0) & 0x7FFFFFFF;
    }

    private static double[] DrawNormal(double mean, double sigma, int n, int seed)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (var i = 0; i < n; i += 2)
        {
            double u1 = 1 - rng.NextDouble(), u2 = 1 - rng.NextDouble();
            var z = Math.Sqrt(-2 * Math.Log(u1));
            x[i] = Math.Max(0, mean + sigma * z * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < n) x[i + 1] = Math.Max(0, mean + sigma * z * Math.Sin(2 * Math.PI * u2));
        }
        return x;
    }

    /// <summary>
    /// 伽马上尾混合：主体正态 + 权重 w 的旧碳长尾（Gamma(k,θ) 平移 minClamp）。
    /// 长尾被完整保留：Q975 与 tailMass 体现偏态，均值不替代分布。
    /// </summary>
    private static double[] DrawGammaMixture(Material m, double mean, double sigma, int n, out double tailMass)
    {
        var k = m.ShapeK ?? 2.0;
        var theta = m.ScaleTheta ?? 400.0;
        var minClamp = m.MinClamp ?? mean;
        var w = Math.Clamp(m.TailWeight ?? 0.12, 0, 1);
        var rng = new Random(StableSeed(m.Id + "|gamma"));
        var x = new double[n];
        var tail = 0;
        var d = k - 1.0 / 3;
        var c = 1 / Math.Sqrt(9 * d);
        for (var i = 0; i < n; i++)
        {
            if (rng.NextDouble() < w)
            {
                // Marsaglia-Tsang gamma
                double v, z;
                do
                {
                    z = StdNormal(rng);
                    v = 1 + c * z;
                } while (v <= 0 || Math.Log(1 - rng.NextDouble()) >= d * (1 - v + Math.Log(v)) + z * z / 2);
                var g = d * v * v * v;
                x[i] = minClamp + theta * g;
                tail++;
            }
            else
            {
                x[i] = Math.Max(0, mean + sigma * StdNormal(rng));
            }
        }
        tailMass = (double)tail / n;
        return x;
    }

    private static double StdNormal(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = 1 - rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
