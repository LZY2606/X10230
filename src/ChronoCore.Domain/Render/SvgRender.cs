using System.Globalization;
using System.Text;
using ChronoCore.Domain.Engine;
using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Render;

/// <summary>Server-side SVG. Every displayed value carries a stable identity
/// via data-id so a round-trip can read back the exact same values in order.
/// No business logic lives in the browser; these are pure projections.</summary>
public static class SvgRender
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Core longitudinal profile: segments, explicit boundaries
    /// (gap/unconformity), anchors with distribution whiskers, events.</summary>
    public static string CoreColumn(CorePackage pkg, GeometryModel geo, int width = 260, int height = 720)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg data-id=\"svg-core-column\" xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"11\">");
        var top = geo.Segments.Count == 0 ? 0 : geo.Segments.Min(s => s.Top);
        var bot = geo.Segments.Count == 0 ? 100 : geo.Segments.Max(s => s.Bottom);
        var span = Math.Max(1e-6, bot - top);
        var pad = 40.0;
        double Y(double depth) => pad + (depth - top) / span * (height - 2 * pad);

        sb.Append($"<text data-id=\"core-title\" x=\"10\" y=\"20\" font-weight=\"bold\">{System.Net.WebUtility.HtmlEncode(pkg.Datum.Name)} 纵剖</text>");
        sb.Append($"<text data-id=\"core-datum\" x=\"10\" y=\"34\">基准: {System.Net.WebUtility.HtmlEncode(pkg.Datum.Id)} 单位: {pkg.Datum.AxisUnit} 向下加深</text>");

        var colX = 90.0; var colW = 46.0;
        var palette = new[] { "#e8e0d0", "#d9c7a3", "#cdb98c", "#bfae84" };
        var idx = 0;
        foreach (var s in geo.Segments)
        {
            var y = Y(s.Top); var h = Math.Max(2, Y(s.Bottom) - y);
            var fill = palette[idx++ % palette.Length];
            sb.Append($"<rect data-id=\"seg-{s.Id}\" x=\"{N(colX)}\" y=\"{N(y)}\" width=\"{N(colW)}\" height=\"{N(h)}\" fill=\"{fill}\" stroke=\"#6b5b3e\"/>");
            sb.Append($"<title data-id=\"seg-{s.Id}-title\">{s.Id} {System.Net.WebUtility.HtmlEncode(s.Raw.Lithology)} {N(s.Top)}–{N(s.Bottom)} cm</title>");
            sb.Append($"<text data-id=\"seg-{s.Id}-label\" x=\"{N(colX + colW + 6)}\" y=\"{N(y + h / 2)}\">{s.Id}</text>");
        }

        foreach (var c in geo.Cuts)
        {
            var y = Y(c.Depth);
            var color = c.Kind == BoundaryKind.Unconformity ? "#b22222" : "#7a4a00";
            var code = c.Kind == BoundaryKind.Unconformity ? "UNCONF" : "GAP";
            sb.Append($"<line data-id=\"cut-{c.Id}\" x1=\"{N(colX - 8)}\" y1=\"{N(y)}\" x2=\"{N(colX + colW + 8)}\" y2=\"{N(y)}\" stroke=\"{color}\" stroke-width=\"3\"/>");
            sb.Append($"<text data-id=\"cut-{c.Id}-label\" x=\"{N(colX - 36)}\" y=\"{N(y + 3)}\" fill=\"{color}\">{code}</text>");
        }

        foreach (var a in geo.Anchors)
        {
            if (!a.DepthOk)
            {
                sb.Append($"<g data-id=\"anchor-{a.Id}-unconverted\"><text x=\"{N(colX + colW + 60)}\" y=\"{height - 12}\" fill=\"#b22222\">{a.Id} 单位不可转换</text></g>");
                continue;
            }
            var y = Y(a.Depth);
            var dist = a.Raw.Age;
            var yLo = Y(a.Depth);
            var yHi = Y(a.Depth); // depth whisker represents sigma in depth? show age sigma as bar length
            var spread = Math.Min(30, Math.Max(4, (dist.SigmaOld + dist.SigmaYoung) * 0.02));
            sb.Append($"<g data-id=\"anchor-{a.Id}\">");
            sb.Append($"<line data-id=\"anchor-{a.Id}-bar\" x1=\"{N(colX + colW + 30)}\" y1=\"{N(yLo - spread)}\" x2=\"{N(colX + colW + 30)}\" y2=\"{N(yHi + spread)}\" stroke=\"#1f5f8b\" stroke-width=\"2\"/>");
            sb.Append($"<circle data-id=\"anchor-{a.Id}-dot\" cx=\"{N(colX + colW + 30)}\" cy=\"{N(y)}\" r=\"4\" fill=\"#1f5f8b\"/>");
            sb.Append($"<text data-id=\"anchor-{a.Id}-label\" x=\"{N(colX + colW + 40)}\" y=\"{N(y + 3)}\">{a.Id}</text>");
            sb.Append($"<title data-id=\"anchor-{a.Id}-raw\">{System.Net.WebUtility.HtmlEncode(a.Raw.Age.Raw)} 储层:{System.Net.WebUtility.HtmlEncode(a.Raw.Age.ReservoirRaw)}</title>");
            sb.Append($"</g>");
        }

        foreach (var ev in pkg.Events)
        {
            if (!UnitSystem.IsDepthConvertible(ev.Depth)) continue;
            var y = Y(UnitSystem.ToCm(ev.Depth));
            sb.Append($"<polygon data-id=\"event-{ev.Id}\" points=\"{N(colX - 6)},{N(y)} {N(colX)},{N(y - 5)} {N(colX)},{N(y + 5)}\" fill=\"#8b1f5f\"/>");
            sb.Append($"<text data-id=\"event-{ev.Id}-label\" x=\"{N(colX - 64)}\" y=\"{N(y + 3)}\" fill=\"#8b1f5f\">{ev.CorrelationId}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Age-depth plot per candidate: median line + 95% uncertainty
    /// band, broken at every section boundary.</summary>
    public static string AgeDepth(CorePackage pkg, CandidateModel cand, int width = 460, int height = 360)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg data-id=\"svg-age-depth-{cand.Id}\" xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"11\">");
        var all = cand.Curves.Values.ToList();
        if (all.Count == 0) { sb.Append($"<text x=\"20\" y=\"40\" data-id=\"age-empty-{cand.Id}\">该候选缺少可绘制连续段</text></svg>"); return sb.ToString(); }
        var minD = all.Min(c => c.DepthsCm.Min()); var maxD = all.Max(c => c.DepthsCm.Max());
        var minA = all.Min(c => c.Q025.Min()); var maxA = all.Max(c => c.Q975.Max());
        var (l, r, t, btm) = (52.0, 16.0, 28.0, 40.0);
        double X(double d) => l + (d - minD) / Math.Max(1e-9, maxD - minD) * (width - l - r);
        double Y(double a) => t + (maxA - a) / Math.Max(1e-9, maxA - minA) * (height - t - btm);

        sb.Append($"<text data-id=\"age-title-{cand.Id}\" x=\"{l}\" y=\"16\" font-weight=\"bold\">候选 {cand.Id} 深度-年龄（不确定度带 p2.5–p97.5）</text>");
        sb.Append($"<line data-id=\"axis-x-{cand.Id}\" x1=\"{N(l)}\" y1=\"{N(height - btm)}\" x2=\"{N(width - r)}\" y2=\"{N(height - btm)}\" stroke=\"#333\"/>");
        sb.Append($"<line data-id=\"axis-y-{cand.Id}\" x1=\"{N(l)}\" y1=\"{N(t)}\" x2=\"{N(l)}\" y2=\"{N(height - btm)}\" stroke=\"#333\"/>");
        sb.Append($"<text data-id=\"xlabel-{cand.Id}\" x=\"{N((l + width - r) / 2 - 40)}\" y=\"{height - 8}\">深度 (cm below datum)</text>");
        sb.Append($"<text data-id=\"ylabel-{cand.Id}\" transform=\"translate(14,{(height / 2)}) rotate(-90)\" text-anchor=\"middle\">年龄 (cal yr BP)</text>");

        foreach (var c in cand.Curves.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            var band = new StringBuilder();
            band.Append($"M {N(X(c.DepthsCm[0]))} {N(Y(c.Q025[0]))} ");
            for (var i = 1; i < c.DepthsCm.Length; i++)
                band.Append($"L {N(X(c.DepthsCm[i]))} {N(Y(c.Q025[i]))} ");
            for (var i = c.DepthsCm.Length - 1; i >= 0; i--)
                band.Append($"L {N(X(c.DepthsCm[i]))} {N(Y(c.Q975[i]))} ");
            band.Append("Z");
            sb.Append($"<path data-id=\"band-{cand.Id}-{c.SectionId}\" d=\"{band}\" fill=\"#1f5f8b\" fill-opacity=\"0.18\" stroke=\"none\"/>");
            var line = new StringBuilder($"M {N(X(c.DepthsCm[0]))} {N(Y(c.Q500[0]))} ");
            for (var i = 1; i < c.DepthsCm.Length; i++)
                line.Append($"L {N(X(c.DepthsCm[i]))} {N(Y(c.Q500[i]))} ");
            sb.Append($"<path data-id=\"median-{cand.Id}-{c.SectionId}\" d=\"{line}\" fill=\"none\" stroke=\"#123f5f\" stroke-width=\"2\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Sedimentation-rate intervals (cm/yr, p2.5–p97.5) across depth.</summary>
    public static string RateBands(CandidateModel cand, int width = 460, int height = 240)
    {
        var sb = new StringBuilder();
        sb.Append($"<svg data-id=\"svg-rate-{cand.Id}\" xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"11\">");
        var all = cand.Curves.Values.ToList();
        if (all.Count == 0) { sb.Append("</svg>"); return sb.ToString(); }
        var minD = all.Min(c => c.DepthsCm.Min()); var maxD = all.Max(c => c.DepthsCm.Max());
        var maxR = all.Max(c => c.RateQ975CmPerYear.Where(v => v > 0 && !double.IsInfinity(v)).DefaultIfEmpty(1).Max());
        var (l, r, t, btm) = (52.0, 16.0, 26.0, 34.0);
        double X(double d) => l + (d - minD) / Math.Max(1e-9, maxD - minD) * (width - l - r);
        double Y(double rr) => t + (1 - Math.Clamp(rr / maxR, 0, 1)) * (height - t - btm);

        sb.Append($"<text data-id=\"rate-title-{cand.Id}\" x=\"{l}\" y=\"16\" font-weight=\"bold\">沉积速率区间 (cm/yr)</text>");
        sb.Append($"<line data-id=\"rate-axis-{cand.Id}\" x1=\"{N(l)}\" y1=\"{N(height - btm)}\" x2=\"{N(width - r)}\" y2=\"{N(height - btm)}\" stroke=\"#333\"/>");
        foreach (var c in cand.Curves.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            for (var i = 1; i < c.DepthsCm.Length; i++)
            {
                if (c.RateQ975CmPerYear[i] <= 0 || double.IsInfinity(c.RateQ975CmPerYear[i])) continue;
                var x = X(c.DepthsCm[i]);
                sb.Append($"<line data-id=\"rate-{cand.Id}-{c.SectionId}-{i}\" x1=\"{N(x)}\" y1=\"{N(Y(c.RateQ975CmPerYear[i]))}\" x2=\"{N(x)}\" y2=\"{N(Y(c.RateQ025CmPerYear[i]))}\" stroke=\"#8b5a1f\" stroke-width=\"1.6\"/>");
                sb.Append($"<circle data-id=\"rate-med-{cand.Id}-{c.SectionId}-{i}\" cx=\"{N(x)}\" cy=\"{N(Y(c.RateQ500CmPerYear[i]))}\" r=\"1.6\" fill=\"#5f3a12\"/>");
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
