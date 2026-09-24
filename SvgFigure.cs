using System.Globalization;
using System.Text;
using DepthAge.Core;

namespace DepthAge.Server;

/// <summary>
/// 服务端 SVG 复核图：岩芯纵剖 + 锚点分布须线 + 多候选曲线 + 不确定度带 + 速率区间。
/// 每个显示值都带稳定身份（锚点引用、候选 Id、事件序号）。
/// </summary>
public static class SvgFigure
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string Render(ProjectedState state, string branchName, bool compareFrozen)
    {
        var branch = state.Branches.FirstOrDefault(b => b.Branch == branchName) ?? state.Branches[0];
        var fit = branch.Fit;
        var sb = new StringBuilder();

        var segments = fit.Model?.Segments ?? (IReadOnlyList<SegmentFit>)Array.Empty<SegmentFit>();
        var depthMax = segments.Count > 0 ? segments.Max(s => s.BottomM) : 10.0;
        var ageValues = new List<double>();
        foreach (var a in branch.Anchors)
            if (a is { Q05: not null, Q95: not null }) { ageValues.Add(a.Q05.Value); ageValues.Add(a.Q95.Value); }
        foreach (var s in segments)
        {
            ageValues.AddRange(s.P05); ageValues.AddRange(s.P95);
            foreach (var c in s.Candidates) ageValues.AddRange(c.AgesKa);
        }
        var frozen = compareFrozen
            ? state.Freezes.LastOrDefault(f => f.Branch == branch.Branch && f.Snapshot is not null)
            : null;
        if (frozen?.Snapshot is { } snap)
            foreach (var s in snap.Segments) { ageValues.AddRange(s.P05); ageValues.AddRange(s.P95); }

        var ageMin = ageValues.Count > 0 ? ageValues.Min() : 0;
        var ageMax = ageValues.Count > 0 ? ageValues.Max() : 1;
        if (ageMax - ageMin < 0.5) ageMax = ageMin + 0.5;
        var agePad = (ageMax - ageMin) * 0.06;
        ageMin -= agePad; ageMax += agePad;

        const double plotLeft = 190, plotRight = 700, plotTop = 80, plotHeightPerM = 52;
        var plotBottom = plotTop + Math.Max(240, depthMax * plotHeightPerM);
        var diagLines = fit.Diagnostics.Count + state.Diagnostics.Count;
        var height = plotBottom + 90 + diagLines * 17;

        double X(double age) => plotLeft + (age - ageMin) / (ageMax - ageMin) * (plotRight - plotLeft);
        double Y(double depth) => plotTop + depth / depthMax * (plotBottom - plotTop);

        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"980\" height=\"{F(height)}\" viewBox=\"0 0 980 {F(height)}\" font-family=\"ui-monospace, Menlo, monospace\" font-size=\"11\">");
        sb.Append("<rect width=\"980\" height=\"100%\" fill=\"#fbfaf7\"/>");

        // 标题：稳定身份（事件序号、分支、参数版本、输入摘要）
        var digest = fit.Model?.InputDigest ?? "none";
        sb.Append($"<text x=\"20\" y=\"24\" font-size=\"15\" font-weight=\"bold\" fill=\"#222\">岩芯年代尺复核 — CORE-A</text>");
        sb.Append($"<text x=\"20\" y=\"44\" fill=\"#555\">as of <tspan font-weight=\"bold\">evt-{state.UpToSeq:D6}</tspan> · branch <tspan font-weight=\"bold\">{E(branch.Branch)}</tspan> · params <tspan font-weight=\"bold\">{E(state.Params.ParamVersion)}</tspan> · model <tspan font-weight=\"bold\">{E(digest)}</tspan> · status {fit.Status}</text>");
        sb.Append($"<text x=\"20\" y=\"60\" fill=\"#777\">深度基准 datum: {E(state.Datum)} · 年龄单位 ka · 速率单位 m/ka{(frozen is not null ? $" · 对比冻结快照 {E(frozen.ModelDigest)} (evt-{frozen.Seq:D6})" : "")}</text>");

        // 坐标轴
        sb.Append($"<line x1=\"{F(plotLeft)}\" y1=\"{F(plotTop)}\" x2=\"{F(plotLeft)}\" y2=\"{F(plotBottom)}\" stroke=\"#999\"/>");
        sb.Append($"<line x1=\"{F(plotLeft)}\" y1=\"{F(plotBottom)}\" x2=\"{F(plotRight)}\" y2=\"{F(plotBottom)}\" stroke=\"#999\"/>");
        sb.Append($"<text x=\"{F((plotLeft + plotRight) / 2)}\" y=\"{F(plotBottom + 30)}\" text-anchor=\"middle\" fill=\"#555\">年龄 Age (ka)</text>");
        sb.Append($"<text x=\"30\" y=\"{F((plotTop + plotBottom) / 2)}\" text-anchor=\"middle\" fill=\"#555\" transform=\"rotate(-90 30 {F((plotTop + plotBottom) / 2)})\">深度 Depth (m, {E(state.Datum)})</text>");
        for (var i = 0; i <= 4; i++)
        {
            var av = ageMin + (ageMax - ageMin) * i / 4;
            sb.Append($"<line x1=\"{F(X(av))}\" y1=\"{F(plotBottom)}\" x2=\"{F(X(av))}\" y2=\"{F(plotBottom + 5)}\" stroke=\"#999\"/>");
            sb.Append($"<text x=\"{F(X(av))}\" y=\"{F(plotBottom + 16)}\" text-anchor=\"middle\" fill=\"#777\">{F(av)}</text>");
        }

        // 岩芯纵剖（左栏）：分段、缺口（斜纹）、不整合（波浪线）
        const double coreLeft = 70, coreWidth = 46;
        sb.Append("<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternTransform=\"rotate(45)\" patternUnits=\"userSpaceOnUse\"><rect width=\"6\" height=\"6\" fill=\"#eee\"/><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#bbb\" stroke-width=\"1.5\"/></pattern></defs>");
        var ordered = segments.OrderBy(s => s.TopM).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            sb.Append($"<rect x=\"{F(coreLeft)}\" y=\"{F(Y(s.TopM))}\" width=\"{F(coreWidth)}\" height=\"{F(Y(s.BottomM) - Y(s.TopM))}\" fill=\"#e8dcc4\" stroke=\"#8a7a55\"/>");
            sb.Append($"<text x=\"{F(coreLeft + coreWidth + 6)}\" y=\"{F((Y(s.TopM) + Y(s.BottomM)) / 2 + 4)}\" fill=\"#6b5d3f\" font-weight=\"bold\">{E(s.Segment)}</text>");
            sb.Append($"<text x=\"{F(coreLeft - 6)}\" y=\"{F(Y(s.TopM) + 4)}\" text-anchor=\"end\" fill=\"#999\" font-size=\"9\">{F(s.TopM)}m</text>");
            sb.Append($"<text x=\"{F(coreLeft - 6)}\" y=\"{F(Y(s.BottomM))}\" text-anchor=\"end\" fill=\"#999\" font-size=\"9\">{F(s.BottomM)}m</text>");
            if (s.BottomBoundary == "gap" && i + 1 < ordered.Count)
            {
                var nextTop = ordered[i + 1].TopM;
                sb.Append($"<rect x=\"{F(coreLeft)}\" y=\"{F(Y(s.BottomM))}\" width=\"{F(coreWidth)}\" height=\"{F(Y(nextTop) - Y(s.BottomM))}\" fill=\"url(#hatch)\" stroke=\"#bbb\" stroke-dasharray=\"3 2\"/>");
                sb.Append($"<text x=\"{F(coreLeft + coreWidth + 6)}\" y=\"{F((Y(s.BottomM) + Y(nextTop)) / 2 + 3)}\" fill=\"#999\" font-size=\"9\">取芯缺口 gap</text>");
            }
            if (s.BottomBoundary == "unconformity")
            {
                var y = Y(s.BottomM);
                var wave = new StringBuilder($"M {F(plotLeft)} {F(y)} ");
                var toggle = 0;
                for (var x = plotLeft; x <= plotRight; x += 12, toggle++)
                    wave.Append($"q 6 {(toggle % 2 == 0 ? -5 : 5)} 12 0 ");
                sb.Append($"<path d=\"{wave}\" fill=\"none\" stroke=\"#b3402e\" stroke-width=\"2\"/>");
                sb.Append($"<text x=\"{F(plotRight + 8)}\" y=\"{F(y + 3)}\" fill=\"#b3402e\" font-size=\"9\">不整合 unconformity</text>");
            }
        }

        // 不确定度带 + 候选曲线（每个层段）
        var candidateColors = new[] { "#1f6fb2", "#4a90c2", "#6ba7cf", "#8dbddb", "#aed3e6", "#c9e2f0" };
        foreach (var s in segments)
        {
            if (s.Status != FitStatus.Ok || s.BandDepthsM.Length == 0)
            {
                if (s.Status == FitStatus.Unsatisfiable)
                {
                    sb.Append($"<rect x=\"{F(plotLeft)}\" y=\"{F(Y(s.TopM))}\" width=\"{F(plotRight - plotLeft)}\" height=\"{F(Y(s.BottomM) - Y(s.TopM))}\" fill=\"#f6e3df\" opacity=\"0.55\"/>");
                    sb.Append($"<text x=\"{F((plotLeft + plotRight) / 2)}\" y=\"{F((Y(s.TopM) + Y(s.BottomM)) / 2)}\" text-anchor=\"middle\" fill=\"#b3402e\">{E(s.Segment)}: 语义无解 UNSATISFIABLE（见诊断）</text>");
                }
                continue;
            }
            var band = new StringBuilder();
            for (var i = 0; i < s.BandDepthsM.Length; i++) band.Append($"{F(X(s.P05[i]))},{F(Y(s.BandDepthsM[i]))} ");
            for (var i = s.BandDepthsM.Length - 1; i >= 0; i--) band.Append($"{F(X(s.P95[i]))},{F(Y(s.BandDepthsM[i]))} ");
            sb.Append($"<polygon points=\"{band}\" fill=\"#1f6fb2\" opacity=\"0.13\"><title>{E(s.Segment)} 90% 不确定度带</title></polygon>");
            var median = new StringBuilder();
            for (var i = 0; i < s.BandDepthsM.Length; i++) median.Append($"{F(X(s.P50[i]))},{F(Y(s.BandDepthsM[i]))} ");
            sb.Append($"<polyline points=\"{median}\" fill=\"none\" stroke=\"#1f6fb2\" stroke-width=\"1\" stroke-dasharray=\"2 2\" opacity=\"0.6\"/>");

            for (var ci = 0; ci < s.Candidates.Count; ci++)
            {
                var c = s.Candidates[ci];
                var pts = new StringBuilder();
                for (var i = 0; i < c.DepthsM.Length; i++) pts.Append($"{F(X(c.AgesKa[i]))},{F(Y(c.DepthsM[i]))} ");
                var color = candidateColors[Math.Min(ci, candidateColors.Length - 1)];
                sb.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{(ci == 0 ? 2.4 : 1.3)}\" opacity=\"{(ci == 0 ? 0.95 : 0.65)}\">" +
                          $"<title>候选 {E(c.Id)} · score {F(c.Score)} · {E(s.Segment)}</title></polyline>");
            }
        }

        // 冻结快照对比（虚线）
        if (frozen?.Snapshot is { } fs)
        {
            foreach (var s in fs.Segments)
            {
                if (s.Candidates.Count == 0) continue;
                var c = s.Candidates[0];
                var pts = new StringBuilder();
                for (var i = 0; i < c.DepthsM.Length; i++) pts.Append($"{F(X(c.AgesKa[i]))},{F(Y(c.DepthsM[i]))} ");
                sb.Append($"<polyline points=\"{pts}\" fill=\"none\" stroke=\"#444\" stroke-width=\"2\" stroke-dasharray=\"7 4\" opacity=\"0.8\">" +
                          $"<title>冻结快照 {E(frozen.ModelDigest)} · {E(s.Segment)} · {E(c.Id)}</title></polyline>");
            }
        }

        // 锚点分布须线（q05–q95 + 中位刻度），事件层为菱形；排除的锚点灰色
        foreach (var a in branch.Anchors.OrderBy(a => a.DepthM ?? double.MaxValue))
        {
            if (a.DepthM is not { } dm || a.Q05 is not { } q05 || a.Q95 is not { } q95 || a.Q50 is not { } q50) continue;
            var color = a.Excluded ? "#aaa" : a.SourceKind == "event-layer" ? "#7a3ea1" : "#c2571a";
            var y = Y(dm);
            sb.Append($"<line x1=\"{F(X(q05))}\" y1=\"{F(y)}\" x2=\"{F(X(q95))}\" y2=\"{F(y)}\" stroke=\"{color}\" stroke-width=\"2\"/>");
            sb.Append($"<line x1=\"{F(X(q05))}\" y1=\"{F(y - 4)}\" x2=\"{F(X(q05))}\" y2=\"{F(y + 4)}\" stroke=\"{color}\" stroke-width=\"1.5\"/>");
            sb.Append($"<line x1=\"{F(X(q95))}\" y1=\"{F(y - 4)}\" x2=\"{F(X(q95))}\" y2=\"{F(y + 4)}\" stroke=\"{color}\" stroke-width=\"1.5\"/>");
            if (a.SourceKind == "event-layer")
                sb.Append($"<rect x=\"{F(X(q50) - 4)}\" y=\"{F(y - 4)}\" width=\"8\" height=\"8\" transform=\"rotate(45 {F(X(q50))} {F(y)})\" fill=\"{color}\"/>");
            else
                sb.Append($"<circle cx=\"{F(X(q50))}\" cy=\"{F(y)}\" r=\"3.2\" fill=\"{color}\"/>");
            var label = $"{a.Ref} · {F(q50)} ka [{F(q05)}–{F(q95)}] @ {F(dm)} m{(a.Excluded ? $" · 已排除({a.Note})" : "")}";
            sb.Append($"<text x=\"{F(X(q95) + 6)}\" y=\"{F(y + 3)}\" fill=\"{color}\" font-size=\"9.5\">{E(label)}</text>");
        }

        // 速率区间面板
        var ry = plotTop + 6;
        sb.Append($"<text x=\"730\" y=\"{F(ry)}\" font-weight=\"bold\" fill=\"#333\">沉积速率区间 (m/ka)</text>");
        ry += 16;
        foreach (var s in segments)
        {
            if (s.Rates.Count == 0) continue;
            var rateText = string.Join(" | ", s.Rates.Select(r => r.ToString()));
            sb.Append($"<text x=\"730\" y=\"{F(ry)}\" fill=\"#1f6fb2\">{E(s.Segment)}: {E(rateText)}</text>");
            ry += 15;
        }
        ry += 8;
        sb.Append($"<text x=\"730\" y=\"{F(ry)}\" font-weight=\"bold\" fill=\"#333\">候选谱系</text>");
        ry += 16;
        foreach (var s in segments)
        {
            foreach (var c in s.Candidates)
            {
                sb.Append($"<text x=\"730\" y=\"{F(ry)}\" fill=\"#555\" font-size=\"9.5\">{E(s.Segment)} · {E(c.Id)} · score {F(c.Score)}</text>");
                ry += 13;
            }
        }

        // 诊断列表（含位置与失效原因）
        var dy = plotBottom + 52;
        sb.Append($"<text x=\"20\" y=\"{F(dy)}\" font-weight=\"bold\" fill=\"#333\">诊断与判定（结构化状态，可重放）</text>");
        dy += 16;
        foreach (var d in state.Diagnostics.Concat(fit.Diagnostics))
        {
            var color = d.Severity == Diagnostic.Error ? "#b3402e" : d.Severity == Diagnostic.Warning ? "#b3741a" : "#4a6b4f";
            sb.Append($"<text x=\"20\" y=\"{F(dy)}\" fill=\"{color}\" font-size=\"10\">[{E(d.Code)}] {E(d.Message)}{(d.Position is not null ? $"  · 位置 {E(d.Position)}" : "")}</text>");
            dy += 17;
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
