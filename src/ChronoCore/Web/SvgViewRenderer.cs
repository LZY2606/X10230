using System.Globalization;
using System.Text;
using ChronoCore.Domain;
using ChronoCore.Engine;

namespace ChronoCore.Web;

/// <summary>
/// 服务端 SVG 渲染。业务规则不藏在浏览器：图形直接由引擎结果绘制，
/// 每个图形元素带 data-dv（稳定显示值身份），并显式画出缺口/不整合边界。
/// </summary>
public static class SvgViewRenderer
{
    private const int Width = 1080;
    private const int Margin = 70;

    public static string Render(DerivationResult result, string candidateId)
    {
        var cand = result.Candidates.FirstOrDefault(c => c.CandidateId == candidateId)
                   ?? result.Candidates.FirstOrDefault();
        var sb = new StringBuilder();
        var depthMin = 0.0;
        var depthMax = Math.Max(20, result.Segments.DefaultIfEmpty().Max(s => s?.BaseDepth ?? 20));
        var coreX = 90; var coreW = 150;
        var anchorX = 320; var anchorW = 230;
        var bandX = 620; var bandW = 240;
        var rateX = 900; var rateW = 130;
        var plotH = 720;

        double Y(double depth) => Margin + (depth - depthMin) / (depthMax - depthMin) * plotH;

        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{plotH + 2 * Margin}\" viewBox=\"0 0 {Width} {plotH + 2 * Margin}\" font-family=\"sans-serif\">");
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"#fafaf7\"/>");
        sb.Append($"<text x=\"{Width / 2}\" y=\"28\" text-anchor=\"middle\" font-size=\"17\" font-weight=\"bold\">BX-2026-C 岩芯纵剖 · 锚点分布 · 速率区间 · 不确定度带（候选 {cand?.CandidateId}）</text>");
        sb.Append($"<text x=\"{Width / 2}\" y=\"48\" text-anchor=\"middle\" font-size=\"11\" fill=\"#555\">结果 {result.ResultId} · 规则 {result.RulesetVersion} · 参数 {result.ParameterSetId} · 输入摘要 {result.InputDigest[..16]}…</text>");

        // 列标题
        foreach (var (x, t) in new[] { (coreX, "岩芯纵剖（分段/边界）"), (anchorX, "锚点年龄分布（Q025–Q975）"), (bandX, "深度-年龄不确定度带"), (rateX, "速率 cm/ka") })
            sb.Append($"<text x=\"{x}\" y=\"{Margin - 12}\" font-size=\"12\" font-weight=\"bold\">{t}</text>");

        // 深度轴
        sb.Append($"<line x1=\"60\" y1=\"{Margin}\" x2=\"60\" y2=\"{Margin + plotH}\" stroke=\"#333\"/>");
        for (var d = 0; d <= depthMax; d += 2)
        {
            var y = Y(d);
            sb.Append($"<line x1=\"55\" y1=\"{y:F1}\" x2=\"60\" y2=\"{y:F1}\" stroke=\"#333\"/>");
            sb.Append($"<text x=\"50\" y=\"{y + 4:F1}\" text-anchor=\"end\" font-size=\"10\">{d}</text>");
        }
        sb.Append($"<text x=\"24\" y=\"{Margin + plotH / 2}\" font-size=\"11\" transform=\"rotate(-90 24 {Margin + plotH / 2})\" text-anchor=\"middle\">深度 mbsf</text>");

        // 纵剖分段
        foreach (var seg in result.Segments)
        {
            var y0 = Y(seg.TopDepth); var y1 = Y(seg.BaseDepth); var h = Math.Max(2, y1 - y0);
            var fill = seg.LowerBoundary == BoundaryKind.Unconformity ? "#e8d8b8"
                       : seg.LowerBoundary == BoundaryKind.Gap ? "#d9d9d9"
                       : !seg.Monotonic ? "#f3c9c9" : "#dce9dc";
            sb.Append($"<rect data-dv=\"dv:segment:{seg.SegmentId}:boundary\" x=\"{coreX}\" y=\"{y0:F1}\" width=\"{coreW}\" height=\"{h:F1}\" fill=\"{fill}\" stroke=\"#5a5a5a\"/>");
            sb.Append($"<text x=\"{coreX + 6}\" y=\"{y0 + 14:F1}\" font-size=\"9\" fill=\"#333\">{seg.SegmentId} {System.Net.WebUtility.HtmlEncode(seg.Lithology)}</text>");
            if (seg.LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity)
            {
                var label = seg.LowerBoundary == BoundaryKind.Gap ? "G1 缺口（显式边界）" : "U1 不整合（显式边界）";
                sb.Append($"<line x1=\"{coreX - 4}\" y1=\"{y1:F1}\" x2=\"{coreX + coreW + 4}\" y2=\"{y1:F1}\" stroke=\"#b03\" stroke-width=\"2.5\" stroke-dasharray=\"6 3\"/>");
                sb.Append($"<text x=\"{coreX + coreW + 8}\" y=\"{y1 + 3:F1}\" font-size=\"9\" fill=\"#b03\">{label}</text>");
            }
            if (!seg.Monotonic)
                sb.Append($"<text x=\"{coreX + 6}\" y=\"{y1 - 4:F1}\" font-size=\"9\" fill=\"#a00\">⚠ 倒置块 R-OVT：不做单调插值</text>");
        }

        // 锚点分布（误差线 = 分布的 Q025/Q975；长尾画红三角）
        var ageMin = result.Anchors.DefaultIfEmpty().Min(a => a?.Q025 ?? 0);
        var ageMax = result.Anchors.DefaultIfEmpty().Max(a => (double?)(a?.Q975 ?? 0)) ?? 20000;
        double AgeXA(double age) => anchorX + (age - ageMin) / Math.Max(1, ageMax - ageMin) * anchorW;
        foreach (var a in result.Anchors.OrderBy(x => x.DepthMbsf))
        {
            var y = Y(a.DepthMbsf);
            var x0 = AgeXA(a.Q025); var x1 = AgeXA(a.Q975); var xm = AgeXA(a.MeanCalYrBp);
            var color = a.LongTail ? "#c0392b" : a.IsEventAnchor ? "#7d3c98" : "#1f618d";
            sb.Append($"<line data-dv=\"dv:anchor:{a.SourceMaterialId}:q025\" x1=\"{x0:F1}\" y1=\"{y:F1}\" x2=\"{x1:F1}\" y2=\"{y:F1}\" stroke=\"{color}\" stroke-width=\"2\"/>");
            sb.Append($"<circle data-dv=\"dv:anchor:{a.SourceMaterialId}:q50\" cx=\"{xm:F1}\" cy=\"{y:F1}\" r=\"4\" fill=\"{color}\"/>");
            sb.Append($"<text x=\"{anchorX - 6}\" y=\"{y + 3:F1}\" text-anchor=\"end\" font-size=\"8.5\" fill=\"#444\">{a.SourceMaterialId.Split('-')[^1]}</text>");
            if (a.LongTail)
                sb.Append($"<polygon data-dv=\"dv:anchor:{a.SourceMaterialId}:tailmass\" points=\"{x1 - 6:F1},{y - 5:F1} {x1 + 6:F1},{y - 5:F1} {x1:F1},{y + 4:F1}\" fill=\"#c0392b\"/>");
            if (a.Note is not null)
                sb.Append($"<title data-dv=\"dv:anchor:{a.SourceMaterialId}:depth\">{System.Net.WebUtility.HtmlEncode(a.Note)}</title>");
        }
        sb.Append($"<text x=\"{anchorX}\" y=\"{Margin + plotH + 22}\" font-size=\"10\">cal yr BP（{ageMin:F0}–{ageMax:F0}）；红=旧碳长尾，紫=事件等时面</text>");

        // 不确定度带（每个段各自坐标，边界处断开）
        if (cand is not null)
        {
            foreach (var segGroup in cand.Band.GroupBy(b => b.SegmentId).OrderBy(g => g.Key))
            {
                var pts = segGroup.OrderBy(b => b.DepthMbsf).ToList();
                if (pts.Count < 2) continue;
                var polyLo = string.Join(" ", pts.Select(b => $"{AgeXA(b.Q025YrBp):F1},{Y(b.DepthMbsf):F1}"));
                var polyHi = string.Join(" ", Enumerable.Reverse(pts).Select(b => $"{AgeXA(b.Q975YrBp):F1},{Y(b.DepthMbsf):F1}"));
                sb.Append($"<polygon points=\"{polyLo} {polyHi}\" fill=\"#2980b9\" fill-opacity=\"0.18\"/>");
                foreach (var b in pts)
                {
                    var dk = b.DepthMbsf.ToString("F3", CultureInfo.InvariantCulture);
                    sb.Append($"<circle data-dv=\"dv:band:{cand.CandidateId}:{b.SegmentId}:{dk}:q50\" cx=\"{AgeXA(b.MedianYrBp):F1}\" cy=\"{Y(b.DepthMbsf):F1}\" r=\"2\" fill=\"#1a5276\"/>");
                }
            }
            sb.Append($"<text x=\"{bandX}\" y=\"{Margin + plotH + 22}\" font-size=\"10\">中位线 + 95% 带；缺口/不整合两侧显式断开，绝不跨边界</text>");

            // 速率区间（水平误差条按相邻锚点行）
            var rates = cand.Rates;
            var rMin = rates.Any() ? rates.Min(r => r.Q025CmPerKa) : 0;
            var rMax = rates.Any() ? rates.Max(r => r.Q975CmPerKa) : 1;
            double RateX(double v) => rateX + (v - rMin) / Math.Max(1e-6, rMax - rMin) * rateW;
            foreach (var r in rates)
            {
                var midDepth = (r.TopDepth + r.BaseDepth) / 2.0;
                var y = Y(midDepth);
                sb.Append($"<line data-dv=\"dv:rate:{cand.CandidateId}:{r.IntervalId}:q025\" x1=\"{RateX(r.Q025CmPerKa):F1}\" y1=\"{y:F1}\" x2=\"{RateX(r.Q975CmPerKa):F1}\" y2=\"{y:F1}\" stroke=\"#117a65\" stroke-width=\"2.5\"/>");
                sb.Append($"<circle data-dv=\"dv:rate:{cand.CandidateId}:{r.IntervalId}:q50\" cx=\"{RateX(r.MedianCmPerKa):F1}\" cy=\"{y:F1}\" r=\"3.5\" fill=\"#0e6251\"/>");
                sb.Append($"<text x=\"{rateX + rateW + 4}\" y=\"{y + 3:F1}\" font-size=\"8.5\">{r.MedianCmPerKa:F1}</text>");
            }
            sb.Append($"<text x=\"{rateX}\" y=\"{Margin + plotH + 22}\" font-size=\"10\">中位速率与 95%（仅段内相邻锚点）</text>");
        }

        // 诊断/失效原因角注
        var errs = result.Diagnostics.Where(d => d.Severity != Severity.Info).Take(6).ToList();
        var dy = Margin + plotH + 46;
        sb.Append($"<text x=\"{coreX}\" y=\"{dy}\" font-size=\"10\" font-weight=\"bold\" fill=\"#922\">诊断与失效原因：</text>");
        var idx = 0;
        foreach (var d in errs)
        {
            idx++;
            var color = d.Severity == Severity.Error ? "#b03a2e" : "#9a7d0a";
            sb.Append($"<text x=\"{coreX}\" y=\"{dy + idx * 15}\" font-size=\"9.5\" fill=\"{color}\">[{d.Code}] {System.Net.WebUtility.HtmlEncode(d.Message.Length > 120 ? d.Message[..120] + "…" : d.Message)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
