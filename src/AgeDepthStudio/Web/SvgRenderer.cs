using System.Globalization;
using System.Text;
using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Web;

/// <summary>
/// 服务端 SVG：岩芯纵剖、锚点分布、速率区间、不确定度带。
/// 每个显示元素携带 data-id 稳定身份，导出/删除/导入后保持不变。
/// </summary>
public static class SvgRenderer
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private const double W = 1080, Margin = 70, Top0 = 24, ColW = 150, ColGap = 46;

    public static string Render(ProjectedState s, ModelResult model)
    {
        var segs = s.Segments
            .Select(seg => (seg, top: Engine.Depth.TryParseMeters(seg.TopDepthRaw, out var t) ? t : 0,
                                  bas: Engine.Depth.TryParseMeters(seg.BaseDepthRaw, out var b) ? b : 0))
            .OrderBy(x => x.top).ToList();
        var dMin = segs.Count > 0 ? segs.First().top : 0;
        var dMax = segs.Count > 0 ? segs.Max(x => x.bas) : 1;
        double Y(double d) => Top0 + (d - dMin) / Math.Max(dMax - dMin, 1e-9) * 560;
        var sb = new StringBuilder();
        sb.Append($"<svg id='age-depth-figure' xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {F(W)} 660' font-family='sans-serif' font-size='11'>");
        sb.Append($"<text x='{Margin}' y='14' data-id='fig.title'>岩芯纵剖 / 锚点分布 / 速率区间 / 不确定度带（服务端渲染）</text>");
        sb.Append($"<text x='{Margin + 400}' y='14' data-id='fig.datum'>深度基准：{Esc(s.Project?.Datum ?? "?")}；时间基准：{Esc(s.Project?.CalendarReference ?? "?")}；年龄单位：{Esc(s.Project?.AgeUnit ?? "?")}</text>");

        // 列 1：岩芯纵剖
        var colX = Margin;
        sb.Append($"<text x='{F(colX)}' y='20' data-id='col.core.label'>岩芯纵剖（深度 {Esc(s.Project?.DepthUnit ?? "m")}）</text>");
        foreach (var (seg, top, bas) in segs)
        {
            var y = Y(top); var h = Math.Max(Y(bas) - y, 2);
            var res = model.Segments.FirstOrDefault(r => r.SegmentId == seg.SegmentId);
            var fill = res?.Outcome switch
            {
                OutcomeKind.Ok => "#dcecf7",
                OutcomeKind.BudgetExhausted => "#f6e0a8",
                OutcomeKind.SemanticUnsolvable => "#f3c2c2",
                OutcomeKind.FormatCorrupt => "#d8d8d8",
                _ => "#eee"
            };
            sb.Append($"<rect id='seg-rect-{seg.SegmentId}' data-id='seg:{seg.SegmentId}' x='{F(colX)}' y='{F(y)}' width='{ColW}' height='{F(h)}' fill='{fill}' stroke='#345'/>");
            sb.Append($"<text x='{F(colX + 6)}' y='{F(y + 14)}' data-id='seg-label:{seg.SegmentId}'>{Esc(seg.SegmentId)} {Esc(seg.Lithology)}</text>");
            sb.Append($"<text x='{F(colX + ColW - 4)}' y='{F(y + h - 4)}' text-anchor='end' data-id='seg-outcome:{seg.SegmentId}'>{res?.Outcome ?? OutcomeKind.Ok}</text>");
            sb.Append($"<text x='{F(colX + ColW + 4)}' y='{F(y + 4)}' data-id='depth-base:{seg.SegmentId}'>{F(bas)}</text>");
            sb.Append($"<text x='{F(colX + ColW + 4)}' y='{F(Math.Min(y + 12, Top0 + 570))}' data-id='depth-top:{seg.SegmentId}'>{F(top)}</text>");
        }
        sb.Append($"<text x='{F(colX + ColW + 4)}' y='{F(Top0 + 572)}' data-id='depth.min'>{F(dMin)}</text>");
        // 缺口：显式边界
        foreach (var g in s.Gaps)
        {
            var below = segs.First(x => x.seg.SegmentId == g.BelowSegmentId);
            var y = Y(below.top);
            var color = g.Kind == GapKind.Unconformity ? "#b12" : "#970";
            sb.Append($"<line id='gap-{g.GapId}' data-id='gap:{g.GapId}' x1='{F(colX - 10)}' y1='{F(y)}' x2='{F(colX + ColW + 10)}' y2='{F(y)}' stroke='{color}' stroke-width='3' stroke-dasharray='8 4'/>");
            sb.Append($"<text x='{F(colX - 12)}' y='{F(y - 3)}' text-anchor='end' data-id='gap-label:{g.GapId}'>{g.Kind} {g.GapId}</text>");
        }
        // 事件层
        foreach (var l in s.Layers)
        {
            if (!Engine.Depth.TryParseMeters(l.DepthRaw, out var d)) continue;
            var y = Y(d);
            sb.Append($"<circle id='layer-{l.LayerId}' data-id='layer:{l.LayerId}' cx='{F(colX + 16)}' cy='{F(y)}' r='5' fill='#0a7' stroke='#053'/>");
            sb.Append($"<text x='{F(colX + 26)}' y='{F(y + 3)}' data-id='layer-label:{l.LayerId}'>{Esc(l.LayerType)} {Esc(l.LayerId)}{(l.CorrelatesTo is null ? "" : "→" + Esc(l.CorrelatesTo))}</text>");
        }
        sb.Append(RenderAnchors(s, model, colX + ColW + ColGap, Y, Y));
        sb.Append(RenderRates(s, model, colX + (ColW + ColGap) * 2 + 120, Y));
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>列 2：锚点分布（q 分位 + 长尾标记）与不确定度带。</summary>
    private static string RenderAnchors(ProjectedState s, ModelResult model, double x, Func<double, double> y, Func<double, double> yDepth)
    {
        var sb = new StringBuilder();
        sb.Append($"<text x='{F(x)}' y='20' data-id='col.anchors.label'>锚点分布与不确定度带</text>");
        // 年龄坐标：按模型分位范围线性映射
        var qs = model.Segments.SelectMany(r => r.Candidates).SelectMany(c => c.Knots)
            .SelectMany(k => new[] { k.Q01, k.Q99 }).ToList();
        double aMin = qs.Count > 0 ? qs.Min() : 0, aMax = qs.Count > 0 ? qs.Max() : 1;
        double X(double a) => x + 40 + (a - aMin) / Math.Max(aMax - aMin, 1e-9) * 220;
        sb.Append($"<line x1='{F(x + 40)}' y1='{F(Top0 + 560)}' x2='{F(x + 260)}' y2='{F(Top0 + 560)}' stroke='#333'/>");
        sb.Append($"<text x='{F(x + 40)}' y='{F(Top0 + 576)}' data-id='age-axis-min'>{F(aMin)}</text>");
        sb.Append($"<text x='{F(x + 260)}' y='{F(Top0 + 576)}' text-anchor='end' data-id='age-axis-max'>{F(aMax)}</text>");

        foreach (var a in s.Anchors.OrderBy(a => a.AnchorId, StringComparer.Ordinal))
        {
            if (!Engine.Depth.TryParseMeters(a.DepthRaw, out var d))
            {
                sb.Append($"<g id='anchor-{a.AnchorId}' data-id='anchor:{a.AnchorId}'><text x='{F(x + 40)}' y='{F(Top0 + 30)}' fill='#a00' data-id='anchor-invalid:{a.AnchorId}'>{Esc(a.AnchorId)}: 深度无法转换（{Esc(a.DepthRaw)}）</text></g>");
                continue;
            }
            var yy = y(d);
            var z = a.Samples.OrderBy(v => v).ToArray();
            double Q(double p) { var hh = (z.Length - 1) * p; var lo = (int)Math.Floor(hh); var f2 = hh - lo; return z[lo] * (1 - f2) + z[Math.Min(lo + 1, z.Length - 1)] * f2; }
            var q01 = Q(.01); var q05 = Q(.05); var q50 = Q(.50); var q95 = Q(.95); var q99 = Q(.99);
            var longTail = (q99 - q50) > s.ActiveParams.LongTailRatio * Math.Max(q50 - q01, 1e-9);
            var excluded = s.Decisions.GroupBy(dd => dd.TargetId)
                .FirstOrDefault(g => g.Key == a.AnchorId)?.OrderBy(dd => dd.DecisionId, StringComparer.Ordinal).Last().Action == DecisionAction.ExcludeAnchor;
            sb.Append($"<g id='anchor-{a.AnchorId}' data-id='anchor:{a.AnchorId}'>");
            sb.Append($"<line x1='{F(X(q01))}' y1='{F(yy)}' x2='{F(X(q99))}' y2='{F(yy)}' stroke='{(excluded ? "#c99" : "#246")}' stroke-width='1' data-id='anchor-q01q99:{a.AnchorId}'/>");
            sb.Append($"<line x1='{F(X(q05))}' y1='{F(yy - 5)}' x2='{F(X(q95))}' y2='{F(yy - 5)}' stroke='{(excluded ? "#c66" : "#069")}' stroke-width='5' opacity='0.45' data-id='anchor-band:{a.AnchorId}'/>");
            sb.Append($"<circle cx='{F(X(q50))}' cy='{F(yy)}' r='3.2' fill='#035' data-id='anchor-q50:{a.AnchorId}'/>");
            sb.Append($"<text x='{F(x)}' y='{F(yy + 3)}' data-id='anchor-label:{a.AnchorId}'>{Esc(a.AnchorId)}{(longTail ? " ⚠长尾" : "")}{(excluded ? " ✕排除" : "")}</text>");
            sb.Append($"<title>{Esc(a.AnchorId)} q01={F(q01)} q50={F(q50)} q99={F(q99)} {Esc(s.Project?.AgeUnit ?? "")}</title></g>");
        }
        // 不确定度带（q01-q99 包络，首候选；不跨越显式边界）
        foreach (var r in model.Segments)
        foreach (var c in r.Candidates.Take(1))
        {
            for (var i = 0; i + 1 < c.Knots.Length; i++)
            {
                var k1 = c.Knots[i]; var k2 = c.Knots[i + 1];
                var yd1 = yDepth(k1.DepthM); var yd2 = yDepth(k2.DepthM);
                var poly = $"{F(X(k1.Q01))},{F(yd1)} {F(X(k2.Q01))},{F(yd2)} {F(X(k2.Q99))},{F(yd2)} {F(X(k1.Q99))},{F(yd1)}";
                sb.Append($"<polygon data-id='uncband:{c.CandidateId}:{i}' points='{poly}' fill='#39c' opacity='0.13'/>");
            }
        }
        return sb.ToString();
    }

    /// <summary>列 3：沉积速率区间（min-max，mm/ka）。</summary>
    private static string RenderRates(ProjectedState s, ModelResult model, double x, Func<double, double> y)
    {
        var sb = new StringBuilder();
        sb.Append($"<text x='{F(x)}' y='20' data-id='col.rates.label'>沉积速率区间</text>");
        var all = model.Segments.SelectMany(r => r.Candidates).SelectMany(c => c.Rates).ToList();
        double rMin = all.Count > 0 ? all.Min(r => r.RateMin) : 0;
        double rMax = all.Count > 0 ? all.Max(r => r.RateMax) : 1;
        double X(double v) => x + Math.Clamp((v - rMin) / Math.Max(rMax - rMin, 1e-9) * 190, 0, 190);
        foreach (var r in model.Segments)
        foreach (var c in r.Candidates.Take(1))
        foreach (var (iv, i) in c.Rates.Select((v, i) => (v, i)))
        {
            var ym = (y(iv.FromDepthM) + y(iv.ToDepthM)) / 2;
            sb.Append($"<g data-id='rate:{c.CandidateId}:{i}'>");
            sb.Append($"<line x1='{F(x + X(iv.RateMin))}' y1='{F(ym)}' x2='{F(x + X(iv.RateMax))}' y2='{F(ym)}' stroke='#740' stroke-width='5' opacity='0.5'/>");
            sb.Append($"<text x='{F(x + X(iv.RateMax) + 4)}' y='{F(ym + 3)}' data-id='rate-text:{c.CandidateId}:{i}'>{F(iv.RateMin)}–{F(iv.RateMax)} mm/ka</text>");
            sb.Append($"</g>");
        }
        return sb.ToString();
    }

    private static string Esc(string? v) => System.Security.SecurityElement.Escape(v ?? "") ?? "";
}
