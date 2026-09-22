using ChronoCore.Domain;

namespace ChronoCore.Engine;

public sealed partial class AgeModelEngine
{
    private static void CollectMeasurementDiagnostics(Material m, List<DiagnosticEntry> loc)
    {
        void Check(Measurement? q, string field)
        {
            if (q is null) return;
            if (string.IsNullOrEmpty(q.Raw))
                loc.Add(new DiagnosticEntry { Code = "E-MISSING", Severity = Severity.Error, Location = $"{m.SourceRef}:{m.SourceLine}/{field}", MaterialId = m.Id, Message = $"{field} 为空，不能以默认值伪装成功" });
            else if (!q.IsConvertible)
            {
                // 无法转换的值留在原位置并附诊断：该材料不参与派生（警告），但绝不丢弃，也不伪装成功
                loc.Add(new DiagnosticEntry { Code = "W-VALUE-UNCONVERTED", Severity = Severity.Warning, Location = $"{m.SourceRef}:{m.SourceLine}/{field}", MaterialId = m.Id, Message = q.Diagnostic ?? "无法转换；原始表示保留在原位置，不参与派生" });
            }
        }
        if (m.Kind == MaterialKind.RadiometricDate) { Check(m.DepthRaw, "depth"); Check(m.AgeRaw, "age"); Check(m.OneSigmaRaw, "sigma"); }
        if (m.Kind == MaterialKind.SegmentGap) { Check(m.TopDepthRaw, "top"); Check(m.BaseDepthRaw, "base"); }
        if (m.Kind == MaterialKind.ReservoirOffset) { Check(m.DeltaR, "deltaR"); Check(m.DeltaRSigma, "sigma"); }
        if (m.Kind == MaterialKind.EventCorrelation) { Check(m.EventDepthRaw, "eventDepth"); Check(m.EventAgeRaw, "eventAge"); Check(m.EventAgeSigmaRaw, "eventSigma"); }
    }

    /// <summary>
    /// 构建显式分段。深度基准、分段缺口、不整合均为不可变材料：
    /// 边界把岩芯纵剖切成互不插值的段；倒置层段保留但标记为不可单调插值。
    /// </summary>
    internal List<SegmentCtx> BuildSegments(IReadOnlyList<Material> active, List<DiagnosticEntry> diag)
    {
        var raw = active.Where(m => m.Kind == MaterialKind.SegmentGap).ToList();
        if (raw.Count == 0)
        {
            diag.Add(new DiagnosticEntry { Code = "E-MISSING", Severity = Severity.Error, Location = "segments", Message = "缺少深度基准/分段材料，无法建立深度坐标" });
            return new List<SegmentCtx>();
        }

        var ordered = raw
            .Select(m => new
            {
                Mat = m,
                Top = m.TopDepthRaw!.ValueSi!.Value,
                Base = m.BaseDepthRaw!.ValueSi!.Value
            })
            .OrderBy(x => x.Top).ThenBy(x => x.Mat.Id, StringComparer.Ordinal)
            .ToList();

        var segments = new List<SegmentCtx>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            var seg = new SegmentCtx
            {
                SegmentId = "seg_" + (i + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                SourceMaterialId = row.Mat.Id,
                Top = row.Top,
                Base = row.Base,
                LowerBoundary = row.Mat.BoundaryAtBase,
                Overturned = row.Mat.OverturnedSection,
                Lithology = row.Mat.Lithology ?? ""
            };
            if (seg.Base < seg.Top)
                diag.Add(new DiagnosticEntry
                {
                    Code = "E-INVERTED", Severity = Severity.Error,
                    Location = $"{row.Mat.SourceRef}:{row.Mat.SourceLine}", MaterialId = row.Mat.Id,
                    Message = $"倒置层段 {seg.SegmentId}（{row.Mat.SegmentCode}）：顶深 {seg.Top} > 底深 {seg.Base}，该段不允许单调插值"
                });
            if (i > 0 && seg.Top < ordered[i - 1].Base - 1e-9 && seg.Top > ordered[i - 1].Top)
                diag.Add(new DiagnosticEntry
                {
                    Code = "E-SEGMENT-OVERLAP", Severity = Severity.Warning,
                    Location = $"{row.Mat.SourceRef}:{row.Mat.SourceLine}", MaterialId = row.Mat.Id,
                    Message = $"分段 {seg.SegmentId} 与前一段深度区间重叠"
                });
            if (seg.LowerBoundary != BoundaryKind.Continuous && i == ordered.Count - 1)
                diag.Add(new DiagnosticEntry { Code = "W-TERMINAL-BOUNDARY", Severity = Severity.Info, Location = seg.SegmentId, Message = "末端边界标记保留，不影响插值" });
            segments.Add(seg);
        }
        return segments;
    }
}
