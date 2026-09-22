using System.Globalization;
using ChronoCore.Domain;
using ChronoCore.Engine;

namespace ChronoCore.Storage;

/// <summary>
/// 审阅界面显示值注册表。身份由内容语义决定（与渲染顺序、运行、项目无关）：
/// 导出→删库→再导入后同值同身份；局部重算只改变受影响身份上的文本。
/// </summary>
public static class DisplayValueRegistry
{
    public static List<DisplayValue> ForResult(DerivationResult r, IReadOnlyList<Material> materials)
    {
        var list = new List<DisplayValue>();
        foreach (var a in r.Anchors)
        {
            var mat = materials.FirstOrDefault(m => m.Id == a.SourceMaterialId);
            list.Add(new DisplayValue
            {
                Id = $"dv:anchor:{a.SourceMaterialId}:depth", Kind = "anchor-depth",
                Numeric = a.DepthMbsf, Text = a.DepthMbsf.ToString("F3", CultureInfo.InvariantCulture),
                Unit = "mbsf", Datum = "mbsf", SourceMaterialId = a.SourceMaterialId, ResultId = r.ResultId,
                InvalidReason = a.SegmentId == "seg_UNBOUNDED" ? "锚点在已声明分段之外" : a.Note
            });
            list.Add(new DisplayValue
            {
                Id = $"dv:anchor:{a.SourceMaterialId}:q50", Kind = "anchor-age-q50",
                Numeric = a.MeanCalYrBp, Text = Math.Round(a.MeanCalYrBp).ToString("F0", CultureInfo.InvariantCulture),
                Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = a.SourceMaterialId, ResultId = r.ResultId,
                InvalidReason = a.Note
            });
            list.Add(new DisplayValue
            {
                Id = $"dv:anchor:{a.SourceMaterialId}:q025", Kind = "anchor-age-q025",
                Numeric = a.Q025, Text = Math.Round(a.Q025).ToString("F0", CultureInfo.InvariantCulture),
                Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = a.SourceMaterialId, ResultId = r.ResultId
            });
            list.Add(new DisplayValue
            {
                Id = $"dv:anchor:{a.SourceMaterialId}:q975", Kind = "anchor-age-q975",
                Numeric = a.Q975, Text = Math.Round(a.Q975).ToString("F0", CultureInfo.InvariantCulture),
                Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = a.SourceMaterialId, ResultId = r.ResultId
            });
            if (a.LongTail)
                list.Add(new DisplayValue
                {
                    Id = $"dv:anchor:{a.SourceMaterialId}:tailmass", Kind = "anchor-tail-mass",
                    Numeric = a.TailMass, Text = a.TailMass.ToString("P1", CultureInfo.InvariantCulture),
                    Unit = "fraction", SourceMaterialId = a.SourceMaterialId, ResultId = r.ResultId
                });
        }
        foreach (var seg in r.Segments)
            list.Add(new DisplayValue
            {
                Id = $"dv:segment:{seg.SegmentId}:boundary", Kind = "segment-boundary",
                Text = seg.LowerBoundary.ToString(), Datum = seg.LowerBoundary == BoundaryKind.Continuous ? "连续" : "显式边界",
                InvalidReason = !seg.Monotonic ? "倒置层段：不声明单调年龄" : null,
                SourceMaterialId = "", ResultId = r.ResultId
            });

        foreach (var cand in r.Candidates)
        {
            foreach (var rate in cand.Rates)
            {
                list.Add(new DisplayValue
                {
                    Id = $"dv:rate:{cand.CandidateId}:{rate.IntervalId}:q50", Kind = "rate-q50",
                    Numeric = rate.MedianCmPerKa, Text = rate.MedianCmPerKa.ToString("F2", CultureInfo.InvariantCulture),
                    Unit = "cm/ka", SourceMaterialId = "", ResultId = r.ResultId
                });
                list.Add(new DisplayValue
                {
                    Id = $"dv:rate:{cand.CandidateId}:{rate.IntervalId}:q025", Kind = "rate-q025",
                    Numeric = rate.Q025CmPerKa, Text = rate.Q025CmPerKa.ToString("F2", CultureInfo.InvariantCulture),
                    Unit = "cm/ka", SourceMaterialId = "", ResultId = r.ResultId
                });
                list.Add(new DisplayValue
                {
                    Id = $"dv:rate:{cand.CandidateId}:{rate.IntervalId}:q975", Kind = "rate-q975",
                    Numeric = rate.Q975CmPerKa, Text = rate.Q975CmPerKa.ToString("F2", CultureInfo.InvariantCulture),
                    Unit = "cm/ka", SourceMaterialId = "", ResultId = r.ResultId
                });
            }
            foreach (var band in cand.Band)
            {
                var depthKey = band.DepthMbsf.ToString("F3", CultureInfo.InvariantCulture);
                list.Add(new DisplayValue
                {
                    Id = $"dv:band:{cand.CandidateId}:{band.SegmentId}:{depthKey}:q50", Kind = "band-q50",
                    Numeric = band.MedianYrBp, Text = Math.Round(band.MedianYrBp).ToString("F0", CultureInfo.InvariantCulture),
                    Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = "", ResultId = r.ResultId
                });
                list.Add(new DisplayValue
                {
                    Id = $"dv:band:{cand.CandidateId}:{band.SegmentId}:{depthKey}:lo", Kind = "band-lo",
                    Numeric = band.Q025YrBp, Text = Math.Round(band.Q025YrBp).ToString("F0", CultureInfo.InvariantCulture),
                    Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = "", ResultId = r.ResultId
                });
                list.Add(new DisplayValue
                {
                    Id = $"dv:band:{cand.CandidateId}:{band.SegmentId}:{depthKey}:hi", Kind = "band-hi",
                    Numeric = band.Q975YrBp, Text = Math.Round(band.Q975YrBp).ToString("F0", CultureInfo.InvariantCulture),
                    Unit = "cal yr BP", Datum = "BP1950", SourceMaterialId = "", ResultId = r.ResultId
                });
            }
        }
        return list;
    }

    /// <summary>
    /// 比较基线与单源改动结果。变化值若属于改动锚点所在段（或锚点自身/其所在段速率与带）
    /// 判为在正确范围内；其它身份的变化是越界影响。
    /// </summary>
    public static List<AffectedDisplayValue> Diff(DerivationResult baseline, DerivationResult variant)
    {
        var scopeSegments = new HashSet<string>(variant.RecomputedSegmentIds);
        var scopeAnchor = variant.Anchors.FirstOrDefault(a => a.SourceMaterialId == variant.ScopeMaterialId);
        if (scopeAnchor is not null) scopeSegments.Add(scopeAnchor.SegmentId);

        var before = ForResult(baseline, Array.Empty<Material>()).ToDictionary(v => v.Id);
        var after = ForResult(variant, Array.Empty<Material>()).ToDictionary(v => v.Id);
        var affected = new List<AffectedDisplayValue>();
        foreach (var (id, b) in before)
        {
            if (!after.TryGetValue(id, out var a)) continue;
            if (b.Text == a.Text && NullableEquals(b.Numeric, a.Numeric)) continue;
            var inScope =
                id.Contains($":{variant.ScopeMaterialId}:") ||
                scopeSegments.Any(s => id.Contains(":" + s + ":"));
            affected.Add(new AffectedDisplayValue
            {
                DisplayId = id, BaselineText = b.Text, VariantText = a.Text, WithinExpectedScope = inScope
            });
        }
        return affected;
    }

    private static bool NullableEquals(double? a, double? b) =>
        a is null && b is null || a is not null && b is not null && Math.Abs(a.Value - b.Value) < 1e-9;
}
