using System.Globalization;
using System.Text;

namespace AgeDepth;

/// <summary>
/// Server-side SVG for the review page: core column, anchor distributions,
/// rate intervals and the uncertainty band. Every displayed value carries a
/// stable identity via data-id.
/// </summary>
public static class Svg
{
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Render(CaseState state)
    {
        var segments = state.Segments.OrderBy(s => s.Top.Value ?? 0).ToList();
        var maxDepth = segments.Select(s => s.Base.Value ?? 0).DefaultIfEmpty(12).Max();
        var maxAge = state.Anchors.SelectMany(a => a.Age.Values).DefaultIfEmpty(35).Max() * 1.08;
        const double top = 30, bottom = 430, plotL = 190, plotR = 560, coreL = 60, coreR = 150;
        double Y(double depth) => top + (bottom - top) * depth / maxDepth;
        double X(double age) => plotL + (plotR - plotL) * age / maxAge;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox='0 0 640 460' xmlns='http://www.w3.org/2000/svg' font-size='10' font-family='monospace'>");
        sb.Append($"<text x='{coreL}' y='16' data-id='datum-depth'>深度基准: mbsf ↓</text>");
        sb.Append($"<text x='{plotL}' y='16' data-id='datum-age'>年龄: ka BP →（分布保留，不用均值替代）</text>");

        // Core column with explicit barriers.
        foreach (var s in segments)
        {
            if (s.Top.Value is null || s.Base.Value is null) continue;
            var y0 = Y(s.Top.Value.Value); var y1 = Y(s.Base.Value.Value);
            var h = Math.Max(y1 - y0, s.IsBarrier ? 6 : 1);
            if (s.IsBarrier)
            {
                var color = s.Kind == "gap" ? "#999" : "#c0392b";
                sb.Append($"<rect x='{coreL}' y='{N(y0)}' width='{coreR - coreL}' height='{N(h)}' fill='{color}' data-id='seg-{s.Id}'>" +
                          $"<title>{s.Id} {s.Kind}（显式边界）{s.Facies}</title></rect>");
                sb.Append($"<line x1='{plotL}' y1='{N(y0)}' x2='{plotR}' y2='{N(y0)}' stroke='{color}' stroke-dasharray='4 3' data-id='boundary-{s.Id}'/>");
            }
            else
            {
                var fill = s.Id switch { "seg-A" => "#e8d8b0", "seg-B" => "#cfd8c0", _ => "#d8c8c0" };
                sb.Append($"<rect x='{coreL}' y='{N(y0)}' width='{coreR - coreL}' height='{N(y1 - y0)}' fill='{fill}' stroke='#666' data-id='seg-{s.Id}'>" +
                          $"<title>{s.Id} {s.Facies} [{s.Top.Raw}–{s.Base.Raw} {s.Top.Unit}]</title></rect>");
                sb.Append($"<text x='{coreL - 4}' y='{N((y0 + y1) / 2)}' text-anchor='end' data-id='label-{s.Id}'>{s.Id}</text>");
            }
        }

        // Depth axis.
        for (var d = 0.0; d <= maxDepth + 0.001; d += 2.0)
            sb.Append($"<text x='{plotR + 6}' y='{N(Y(d))}' data-id='depthtick-{N(d)}'>{N(d)} m</text>");

        // Uncertainty bands + median lines per accepted candidate.
        var palette = new[] { "#1f77b4", "#2ca02c", "#9467bd", "#8c564b" };
        var ci = 0;
        foreach (var sr in state.Computation.Segments)
        foreach (var c in sr.Candidates)
        {
            if (c.Status != "accepted") continue;
            var color = palette[ci++ % palette.Length];
            // Band polygon: p05 down one side, p95 back up, interpolated between knots.
            var pts = new List<(double age, double depth)>();
            for (var i = 0; i < c.DepthKnots.Length; i++) pts.Add((c.AgeP05[i], c.DepthKnots[i]));
            for (var i = c.DepthKnots.Length - 1; i >= 0; i--) pts.Add((c.AgeP95[i], c.DepthKnots[i]));
            if (pts.Count >= 2)
            {
                var poly = string.Join(" ", pts.Select(p => $"{N(X(p.age))},{N(Y(p.depth))}"));
                sb.Append($"<polygon points='{poly}' fill='{color}' opacity='0.18' data-id='band-{c.Id}'>" +
                          $"<title>不确定度带 {c.Id}（p05–p95，{c.SegmentId}）</title></polygon>");
            }
            var line = string.Join(" ", c.DepthKnots.Select((d, i) => $"{N(X(c.AgeMedian[i]))},{N(Y(d))}"));
            sb.Append($"<polyline points='{line}' fill='none' stroke='{color}' stroke-width='1.6' data-id='median-{c.Id}'>" +
                      $"<title>候选 {c.Id} 中位线 — {c.Reason}</title></polyline>");
        }

        // Rejected candidates kept visible as dashed grey.
        foreach (var c in state.Computation.AllCandidates.Where(c => c.Status == "rejected"))
        {
            var line = string.Join(" ", c.DepthKnots.Select((d, i) => $"{N(X(c.AgeMedian[i]))},{N(Y(d))}"));
            sb.Append($"<polyline points='{line}' fill='none' stroke='#888' stroke-dasharray='2 3' data-id='rejected-{c.Id}'>" +
                      $"<title>未采纳候选 {c.Id}（保留）— {c.Reason}</title></polyline>");
        }

        // Anchors: distribution whiskers (p05–p95), never a bare mean.
        foreach (var a in state.Anchors)
        {
            if (a.Depth.Value is null)
            {
                sb.Append($"<text x='{coreR + 4}' y='{N(bottom + 12)}' fill='#c0392b' data-id='anchor-{a.Id}'>" +
                          $"⚠ {a.Id}: 深度 '{a.Depth.Raw}' 无法转换，保留原位</text>");
                continue;
            }
            var y = Y(a.Depth.Value.Value);
            var offset = a.ReservoirOffset is { Convertible: true, Value: not null } ? a.ReservoirOffset.Value.Value : 0.0;
            var p05 = a.Age.Quantile(0.05) - offset; var p95 = a.Age.Quantile(0.95) - offset;
            var med = a.Age.Median - offset;
            sb.Append($"<g data-id='anchor-{a.Id}'>" +
                      $"<line x1='{N(X(p05))}' y1='{N(y)}' x2='{N(X(p95))}' y2='{N(y)}' stroke='#333' stroke-width='2'/>" +
                      $"<circle cx='{N(X(med))}' cy='{N(y)}' r='2.6' fill='#000'/>" +
                      $"<title>{a.Id} @{a.Depth.Raw} {a.Depth.Unit}｜{a.Age.Values.Length} 个分布样本" +
                      $"{(offset != 0 ? $"｜储层偏移 -{a.ReservoirOffset!.Raw} {a.ReservoirOffset.Unit}" : "")}｜{a.Source}</title></g>");
        }

        // Event layers.
        foreach (var l in state.Layers)
        {
            if (l.Depth.Value is null) continue;
            var y = Y(l.Depth.Value.Value);
            sb.Append($"<line x1='{plotL}' y1='{N(y)}' x2='{plotR}' y2='{N(y)}' stroke='#e67e22' stroke-width='1.4' data-id='layer-{l.Id}'>" +
                      $"<title>{l.Name} → {l.CorrelatedEventId}</title></line>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string RateTable(CaseState state)
    {
        var sb = new StringBuilder("<table><tr><th>候选</th><th>区间 (mbsf)</th><th>速率中位 (ka/m)</th><th>速率区间</th></tr>");
        foreach (var c in state.Computation.AllCandidates.Where(c => c.Status == "accepted"))
        foreach (var r in c.Rates)
            sb.Append($"<tr data-id='rate-{c.Id}-{N(r.TopDepth)}'><td>{c.Id}</td>" +
                      $"<td>{N(r.TopDepth)}–{N(r.BaseDepth)}</td><td>{N(r.MedianKaPerM)}</td>" +
                      $"<td>[{N(r.MinKaPerM)}, {N(r.MaxKaPerM)}]</td></tr>");
        return sb.Append("</table>").ToString();
    }
}
