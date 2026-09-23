using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

public sealed record PreparedAnchor
{
    public required AnchorMaterial Anchor { get; init; }
    public required double DepthM { get; init; }
    public List<double> Samples { get; init; } = new();
    public List<QuantilePoint> Quantiles { get; init; } = new();
    public required bool Ready { get; init; }
    public List<string> Diagnostics { get; init; } = new();
}

/// <summary>
/// 派生层规范化：原始表示保留在材料里，这里只生成日历年龄分布样本。
/// 任何无法转换的值留在原位置并附诊断，绝不静默丢弃。
/// </summary>
public sealed class MaterialPreparation
{
    private readonly UnitRegistry _units;

    public MaterialPreparation(UnitRegistry? units = null) => _units = units ?? new UnitRegistry();

    public Dictionary<string, PreparedAnchor> Prepare(ProjectState state, RuleBook rules)
    {
        var result = new Dictionary<string, PreparedAnchor>();
        var readyCurves = state.CalibrationCurves.Values.ToDictionary(c => c.Id, c => c);

        foreach (var anchor in state.Anchors.Values.OrderBy(a => a.Id))
        {
            var diagnostics = new List<string>();
            var depthM = ResolveDepth(state, anchor, diagnostics);
            var prepared = new PreparedAnchor { Anchor = anchor, DepthM = depthM, Ready = false, Diagnostics = diagnostics };

            if (anchor.Excluded)
            {
                diagnostics.Add("锚点已被排除决定排除（材料保留，不参与建模）");
                result[anchor.Id] = prepared;
                continue;
            }
            if (depthM is double.NaN)
            {
                result[anchor.Id] = prepared;
                continue;
            }

            switch (anchor.Age.Kind)
            {
                case AgeKind.Calendar:
                    FillCalendar(prepared, rules);
                    break;
                case AgeKind.EventDerived:
                    FillEventDerived(state, prepared);
                    break;
                case AgeKind.Radiocarbon:
                    FillRadiocarbon(state, readyCurves, prepared, rules);
                    break;
            }
            result[anchor.Id] = prepared;
        }
        return result;
    }

    private static double ResolveDepth(ProjectState state, AnchorMaterial anchor, List<string> diagnostics)
    {
        var q = anchor.Depth.Quantity;
        if (!q.IsUsable)
        {
            diagnostics.AddRange(q.Diagnostics.Select(d => $"深度：{d}"));
            return double.NaN;
        }
        var depthM = q.RequireCanonical();
        if (state.Datums.TryGetValue(anchor.Depth.DatumId, out var datum))
            depthM += datum.ZeroOffsetToCanonicalM;
        else
            diagnostics.Add($"深度基准 '{anchor.Depth.DatumId}' 未找到：保留原始表示，位置不能确定");
        return depthM;
    }

    private static void FillCalendar(PreparedAnchor prepared, RuleBook rules)
    {
        var age = prepared.Anchor.Age;
        if (age.SamplesCalYrBP.Count >= 8)
        {
            Assign(prepared, age.SamplesCalYrBP);
            return;
        }
        if (age.ReportedAge.IsUsable && age.ReportedSigma is { } sigma && sigma > 0)
        {
            var mean = age.ReportedAge.RequireCanonical();
            var seed = SeedFor(prepared.Anchor.Id, mean);
            var rng = new DeterministicRng(seed);
            var samples = Enumerable.Range(0, (int)rules.MaxSamplesPerAnchor)
                .Select(_ => mean + sigma * rng.NextNormal())
                .Where(v => v >= 0).ToList();
            Assign(prepared, samples);
            return;
        }
        prepared.Diagnostics.Add("日历年龄缺少可用分布（既无样本也无 sigma）：不能用均值替代分布");
    }

    private static void FillEventDerived(ProjectState state, PreparedAnchor prepared)
    {
        if (prepared.Anchor.EventBedId is not { } eventId || !state.EventBeds.TryGetValue(eventId, out var bed))
        {
            prepared.Diagnostics.Add($"事件层 '{prepared.Anchor.EventBedId}' 缺失：事件相关锚点无法取得共享年龄分布");
            return;
        }
        if (bed.Age.SamplesCalYrBP.Count < 8)
        {
            prepared.Diagnostics.Add($"事件层 '{eventId}' 年龄分布不足：保留原值，不参与建模");
            return;
        }
        Assign(prepared, bed.Age.SamplesCalYrBP.ToList());
    }

    private void FillRadiocarbon(ProjectState state, Dictionary<string, CalibrationCurve> curves, PreparedAnchor prepared, RuleBook rules)
    {
        var age = prepared.Anchor.Age;
        if (age.CalibrationCurveId is not { } curveId || !curves.TryGetValue(curveId, out var curve))
        {
            prepared.Diagnostics.Add($"校准曲线 '{age.CalibrationCurveId}' 未提供：14C 年龄不能线性当作日历年龄");
            return;
        }
        if (!age.ReportedAge.Value.HasValue)
        {
            prepared.Diagnostics.AddRange(age.ReportedAge.Diagnostics);
            return;
        }

        var mean14C = age.ReportedAge.Value.Value;
        var sigma14C = age.ReportedSigma ?? 0;
        var dR = 0.0;
        var dRSigma = 0.0;
        var offset = state.ReservoirOffsets.Values.FirstOrDefault(o => o.AppliesTo(prepared.DepthM));
        if (offset is not null)
        {
            dR = offset.DeltaRYr;
            dRSigma = offset.SigmaYr;
            prepared.Diagnostics.Add($"应用储层偏移 {offset.Id}：ΔR={offset.DeltaRYr}±{offset.SigmaYr} yr（先扣减再校准）");
        }
        var effectiveMean = mean14C - dR;
        var effectiveSigma = Math.Sqrt(sigma14C * sigma14C + dRSigma * dRSigma);

        var seed = SeedFor(prepared.Anchor.Id, effectiveMean) ^ (rules.SamplingSeedBase & 0x7fffffff);
        if (curve.TryCalibrate(effectiveMean, effectiveSigma, seed, out var samples, out var quantiles, out var diagnostic))
        {
            Assign(prepared, samples);
            if (diagnostic is not null) prepared.Diagnostics.Add(diagnostic);
        }
        else
        {
            prepared.Diagnostics.Add(diagnostic ?? "校准失败");
        }
    }

    private static void Assign(PreparedAnchor prepared, List<double> samples)
    {
        var ordered = samples.OrderBy(v => v).ToList();
        prepared.Samples = ordered;
        double[] ps = { 0.025, 0.05, 0.16, 0.25, 0.5, 0.75, 0.84, 0.95, 0.975 };
        prepared.Quantiles = ps.Select(p => new QuantilePoint(p, CalibrationCurve.Quantile(ordered, p))).ToList();
        prepared.Ready = true;
    }

    public static int SeedFor(string key, double salt)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{key}|{salt:R}"));
        return Math.Abs(BitConverter.ToInt32(bytes, 0)) | 1;
    }
}
