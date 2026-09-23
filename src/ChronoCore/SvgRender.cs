using System.Globalization;
using System.Text;

namespace ChronoCore;

/// <summary>
/// 服务端 SVG 渲染：岩芯纵剖 + 锚点年龄分布 + 速率区间 + 不确定度带。
/// 每个可见元素都带稳定 id（data-stable-id），导出/再导入后身份不变。
/// </summary>
public static class SvgRender
{
    private const int W = 980;
    private const int Top0 = 40;

    public static string RenderProfile(ProjectState s, DerivedModel? model, string branchId)
    {
        var allTops = s.IntervalOrder.Select(i => i.Top).Where(q => q.IsConvertible).Select(q => q.Value!.Value);
        var allBots = s.IntervalOrder.Select(i => i.Bottom).Where(q => q.IsConvertible).Select(q => q.Value!.Value);
        if (!allTops.Any() || !allBots.Any()) return EmptySvg("没有可绘制的连续段（深度无法转换时保留诊断而非丢弃）");
        var minD = allTops.Min(); var maxD = allBots.Max();
        var span = Math.Max(maxD - minD, 1);
        var h = (int)(Top0 + span * 26 + 120);

        double Y(double depth) => Top0 + (depth - minD) / span * (h - Top0 - 90);

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{W}' height='{h}' viewBox='0 0 {W} {h}' ");
        sb.Append($"font-family='ui-monospace,Menlo,monospace' font-size='11'>");
        sb.Append($"<text x='16' y='22' data-stable-id='title-{branchId}' font-size='15' font-weight='700'>");
        sb.Append($"CORESITE-7B 岩芯纵剖（深度基准 MBSF，米；年龄 cal a BP） 分支:{branchId}</text>");

        // 连续段柱
        const double xCol = 90, wCol = 70;
        foreach (var iv in s.IntervalOrder)
        {
            if (!iv.Top.IsConvertible || !iv.Bottom.IsConvertible) continue;
            var y1 = Y(iv.Top.Value!.Value); var y2 = Y(iv.Bottom.Value!.Value);
            sb.Append($"<rect data-stable-id='profile-{iv.Id}' x='{xCol}' y='{y1:F1}' width='{wCol}' ")
              .Append($"height='{(y2 - y1):F1}' fill='#e8f1e6' stroke='#3f7d4f' stroke-width='1.5'/>");
            sb.Append($"<text data-stable-id='label-{iv.Id}' x='{xCol + wCol + 8}' y='{(y1 + y2) / 2:F1}' fill='#274d30'>")
              .Append($"{iv.Id} {iv.Name}</text>");
            sb.Append($"<text x='{xCol - 8}' y='{y1 + 4:F1}' text-anchor='end' fill='#555'>{iv.Top.Value!.Value:0.##} m</text>");
            sb.Append($"<text x='{xCol - 8}' y='{y2 + 4:F1}' text-anchor='end' fill='#555'>{iv.Bottom.Value!.Value:0.##} m</text>");
        }
        // 显式缺口 / 不整合
        foreach (var g in s.GapOrder)
        {
            var qA = Quantity.Depth(g.AboveDepthRaw, g.DatumId);
            var qB = Quantity.Depth(g.BelowDepthRaw, g.DatumId);
            if (!qA.IsConvertible || !qB.IsConvertible) continue;
            var yA = Y(qA.Value!.Value); var yB = Y(qB.Value!.Value);
            var color = g.Type == "unconformity" ? "#b3472f" : "#8a6d1f";
            var pat = g.Type == "unconformity"
                ? "M82 " + (yA + 3).ToString("F1") + " l18 8 -18 8 18 8 -18 8 18 8"
                : "";
            sb.Append($"<g data-stable-id='gap-{g.Id}'>");
            sb.Append($"<rect x='{xCol}' y='{yA:F1}' width='{wCol}' height='{(yB - yA):F1}' fill='#fff3d6' stroke='{color}' stroke-dasharray='4 3'/>");
            if (g.Type == "unconformity")
                sb.Append($"<path d='{pat}' fill='none' stroke='{color}' stroke-width='2'/>");
            sb.Append($"<text x='{xCol + wCol + 8}' y='{(yA + yB) / 2:F1}' fill='{color}' font-weight='700'>")
              .Append($"{g.Id} {g.Type}（显式边界，不插值）</text>");
            sb.Append($"</g>");
        }

        // 锚点：分布须 + 中位数点 + 原始表示
        const double xDist = 300, scale = 0.0045;
        foreach (var a in s.AnchorOrder)
        {
            var dq = Quantity.Depth(a.DepthRaw, a.DatumId);
            if (!dq.IsConvertible)
            {
                var yy = Y(minD + span * 0.5);
                sb.Append($"<g data-stable-id='anchor-{a.Id}' data-invalid='true'>")
                  .Append($"<circle cx='{xDist}' cy='{yy}' r='6' fill='none' stroke='#c00' stroke-width='2'/>")
                  .Append($"<text x='{xDist + 12}' y='{yy + 4}' fill='#c00'>{a.SampleCode} 深度原值 “{a.DepthRaw}” 不可转换：{dq.DiagnosticCode}（保留原位）</text></g>");
                continue;
            }
            var y = Y(dq.Value!.Value);
            var excluded = s.Exclusions.Values.Any(e => e.MaterialId == a.Id);
            var med = a.Samples.Median;
            var lo = a.Samples.Q025; var hi = a.Samples.Q975;
            var lo99 = a.Samples.Q005; var hi99 = a.Samples.Q995;
            var xm = xDist + (med - 0) * scale;
            var xlo95 = xDist + lo * scale; var xhi95 = xDist + hi * scale;
            var xlo99 = xDist + lo99 * scale; var xhi99 = xDist + hi99 * scale;
            var op = excluded ? 0.28 : 1;
            sb.Append($"<g data-stable-id='anchor-{a.Id}' data-excluded='{(excluded ? "true" : "false")}' opacity='{op}'>");
            sb.Append($"<line data-stable-id='anchor-{a.Id}-q99' x1='{xlo99:F1}' y1='{y}' x2='{xhi99:F1}' y2='{y}' stroke='#c98ad8' stroke-width='5' stroke-linecap='round'/>");
            sb.Append($"<line data-stable-id='anchor-{a.Id}-q95' x1='{xlo95:F1}' y1='{y}' x2='{xhi95:F1}' y2='{y}' stroke='#5b7ec4' stroke-width='2.5' stroke-linecap='round'/>");
            sb.Append($"<circle data-stable-id='anchor-{a.Id}-median' cx='{xm:F1}' cy='{y}' r='3.2' fill='#1b3a6b'/>");
            sb.Append($"<text x='{xDist - 8}' y='{y - 6}' text-anchor='end' fill='#333'>{a.SampleCode}</text>");
            sb.Append($"<text x='{xDist - 8}' y='{y + 6}' text-anchor='end' fill='#888'>{a.DepthRaw}</text>");
            if (excluded)
                sb.Append($"<text x='{xhi99 + 6:F1}' y='{y + 4}' fill='#c00'>已排除（决定保留）</text>");
            sb.Append($"</g>");
        }
        sb.Append($"<text x='{xDist}' y='{h - 50}' fill='#555'>← 年龄 cal a BP（分布：紫=99%，蓝=95%，点=中位数；长尾 df≤8 时 99% 明显更宽）</text>");

        // 不确定度带（每段独立，缺口处断开）
        if (model is not null)
        {
            const double xEnv = 560, envScale = 0.0014, envW = 300;
            foreach (var iv in s.IntervalOrder.Where(i => i.Top.IsConvertible && i.Bottom.IsConvertible))
            {
                var pts = model.Envelope.Where(e => e.IntervalId == iv.Id).OrderBy(e => e.DepthM).ToList();
                if (pts.Count < 2) continue;
                var y1 = Y(iv.Top.Value!.Value); var y2 = Y(iv.Bottom.Value!.Value);
                sb.Append($"<g data-stable-id='env-band-{iv.Id}-{model.Id}'>");
                // 99% 带
                sb.Append("<polygon fill='#f3d9f7' fill-opacity='0.55' points='");
                foreach (var p in pts) sb.Append($"{(xEnv + p.High99 * envScale):F1},{Y(p.DepthM):F1} ");
                for (var k = pts.Count - 1; k >= 0; k--)
                    sb.Append($"{(xEnv + pts[k].Low99 * envScale):F1},{Y(pts[k].DepthM):F1} ");
                sb.Append("'/>");
                // 95% 带
                sb.Append("<polygon fill='#9fc0e8' fill-opacity='0.6' points='");
                foreach (var p in pts) sb.Append($"{(xEnv + p.High95 * envScale):F1},{Y(p.DepthM):F1} ");
                for (var k = pts.Count - 1; k >= 0; k--)
                    sb.Append($"{(xEnv + pts[k].Low95 * envScale):F1},{Y(pts[k].DepthM):F1} ");
                sb.Append("'/>");
                // 中位线
                sb.Append("<polyline fill='none' stroke='#0d3b66' stroke-width='2' points='");
                foreach (var p in pts) sb.Append($"{(xEnv + p.MedianAge * envScale):F1},{Y(p.DepthM):F1} ");
                sb.Append("'/>");
                sb.Append($"</g>");
            }

            // 速率区间（相邻锚点之间，m/Ma 标注）
            const double xRate = 880;
            foreach (var r in model.Rates)
            {
                var ym = Y((r.DepthFromM + r.DepthToM) / 2);
                var lo = r.RateLow * 1_000_000; var hi = r.RateHigh * 1_000_000; var md = r.RateMedian * 1_000_000;
                sb.Append($"<g data-stable-id='{r.StableId}' data-valid='{(r.Valid ? "true" : "false")}'>");
                if (r.Valid)
                {
                    sb.Append($"<line x1='{xRate}' y1='{ym}' x2='{xRate + 60}' y2='{ym}' stroke='#777'/>");
                    sb.Append($"<line x1='{xRate + (lo / Math.Max(hi, 1e-9)) * 60:F1}' y1='{ym - 4}' x2='{xRate + (lo / Math.Max(hi, 1e-9)) * 60:F1}' y2='{ym + 4}' stroke='#1b6e3a'/>");
                    sb.Append($"<circle cx='{xRate + (md / Math.Max(hi, 1e-9)) * 60:F1}' cy='{ym}' r='3' fill='#1b6e3a'/>");
                    sb.Append($"<text x='{xRate}' y='{ym - 6}' fill='#1b6e3a'>{lo:0}–{hi:0} m/Ma</text>");
                }
                else sb.Append($"<text x='{xRate}' y='{ym}' fill='#c00'>速率无定义(95%重叠)</text>");
                sb.Append($"</g>");
            }
            sb.Append($"<text x='{xEnv}' y='{h - 50}' fill='#555'>不确定度带（粉=99%/蓝=95%，显式边界处断开）</text>");
            sb.Append($"<text x='{xRate}' y='{h - 50}' fill='#555'>速率区间 m/Ma</text>");

            sb.Append($"<text x='16' y='{h - 20}' data-stable-id='model-{model.Id}' fill='#333'>")
              .Append($"当前模型 {model.Id}｜{model.Label}｜rank={model.Rank}｜tie={model.Tie}｜")
              .Append($"输入摘要 {model.InputDigest[..10]}…｜参数 {model.ParameterVersion}｜")
              .Append($"合法={model.SemanticallyValid}｜{model.Notes}</text>");
        }
        else
        {
            sb.Append($"<text x='300' y='{h - 30}' fill='#a05a00'>当前事件点尚未派生模型（或派生结果为语义无解/预算耗尽，详见事件流诊断）</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string EmptySvg(string msg)
        => $"<svg xmlns='http://www.w3.org/2000/svg' width='700' height='120'>" +
           $"<text x='20' y='60' fill='#c00'>{System.Net.WebUtility.HtmlEncode(msg)}</text></svg>";
}
