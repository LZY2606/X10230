using System.Globalization;
using System.Text;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.Rendering;

public static class SvgProfile
{
    public static string Render(ReviewModel model)
    {
        var sb = new StringBuilder();
        int width = 520, height = 720, marginLeft = 64, marginTop = 40;
        double top = model.Sections.Min(s => s.TopMeters);
        double bottom = model.Sections.Max(s => s.BottomMeters);
        double span = bottom - top;
        double Y(double d) => marginTop + (d - top) / span * (height - 80);
        double coreX = marginLeft + 40, coreW = 70;

        sb.Append(Svg.Head(width, height, "svg-core-profile"));
        sb.Append($"<text id=\"profile-title\" x=\"{marginLeft}\" y=\"22\" class=\"title\">Core vertical profile (depth axis: {model.VerticalDatum})</text>");

        foreach (var s in model.Sections)
        {
            double y1 = Y(s.TopMeters), y2 = Y(s.BottomMeters);
            sb.Append($"<rect id=\"profile-{s.Id}\" data-stable-id=\"profile-{s.Id}\" " +
                      $"x=\"{coreX.ToString(CultureInfo.InvariantCulture)}\" y=\"{y1.ToString(CultureInfo.InvariantCulture)}\" " +
                      $"width=\"{coreW}\" height=\"{(y2 - y1).ToString(CultureInfo.InvariantCulture)}\" " +
                      $"class=\"section\" fill=\"#eef2f6\" stroke=\"#48545f\"/>");
            sb.Append($"<text x=\"{(coreX + coreW + 8).ToString(CultureInfo.InvariantCulture)}\" " +
                      $"y=\"{((y1 + y2) / 2 + 4).ToString(CultureInfo.InvariantCulture)}\" class=\"label\">{Svg.Escape(s.Name)}</text>");
        }

        foreach (var b in model.Boundaries)
        {
            double y = Y(b.DepthMeters);
            string cls = b.Kind switch
            {
                "Unconformity" => "boundary-unconf",
                "CoringGap" => "boundary-gap",
                "DuplicateDepthMarker" => "boundary-dup",
                _ => "boundary-inv"
            };
            sb.Append($"<line id=\"profile-{b.Id}\" data-stable-id=\"profile-{b.Id}\" " +
                      $"x1=\"{coreX - 10}\" y1=\"{y.ToString(CultureInfo.InvariantCulture)}\" " +
                      $"x2=\"{coreX + coreW + 10}\" y2=\"{y.ToString(CultureInfo.InvariantCulture)}\" class=\"{cls}\"/>");
            sb.Append($"<text id=\"profile-{b.Id}-label\" x=\"{coreX + coreW + 14}\" " +
                      $"y=\"{(y + 3).ToString(CultureInfo.InvariantCulture)}\" class=\"small\">{Svg.Escape(b.Label)}</text>");
        }

        foreach (var e in model.EventBeds)
        {
            double y = Y(e.DepthMeters);
            sb.Append($"<rect id=\"profile-{e.Id}\" data-stable-id=\"profile-{e.Id}\" " +
                      $"x=\"{coreX}\" y=\"{y.ToString(CultureInfo.InvariantCulture)}\" width=\"{coreW}\" height=\"6\" " +
                      $"class=\"eventbed\" fill=\"#d98a3d\" stroke=\"#8a4b12\"/>");
            sb.Append($"<text x=\"{marginLeft - 6}\" y=\"{(y + 3).ToString(CultureInfo.InvariantCulture)}\" " +
                      $"text-anchor=\"end\" class=\"small\">{Svg.Escape(e.CorrelationKey)}</text>");
            if (e.Unconverted)
                sb.Append($"<text x=\"{coreX + coreW + 14}\" y=\"{(y + 14).ToString(CultureInfo.InvariantCulture)}\" " +
                          $"class=\"diag\">raw '{Svg.Escape(e.RawDepthText)}' not converted (kept in place)</text>");
        }

        for (double d = Math.Ceiling(top); d <= bottom; d += 0.5)
        {
            double y = Y(d);
            sb.Append($"<line x1=\"{marginLeft + 30}\" y1=\"{y.ToString(CultureInfo.InvariantCulture)}\" " +
                      $"x2=\"{coreX + coreW + 4}\" y2=\"{y.ToString(CultureInfo.InvariantCulture)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{marginLeft + 24}\" y=\"{(y + 3).ToString(CultureInfo.InvariantCulture)}\" " +
                      $"text-anchor=\"end\" class=\"small\">{d.ToString("0.0", CultureInfo.InvariantCulture)} m</text>");
        }

        sb.Append($"<text x=\"{marginLeft}\" y=\"{height - 14}\" class=\"small\">" +
                  $"Depth datum: {model.VerticalDatum}; time datum: {model.TimeDatum}; parameters {model.ParametersVersion}</text>");
        sb.Append(Svg.Foot());
        return sb.ToString();
    }
}

public static class Svg
{
    public static string Head(int width, int height, string rootId) =>
        $"<svg id=\"{rootId}\" data-stable-id=\"{rootId}\" xmlns=\"http://www.w3.org/2000/svg\" " +
        $"viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" " +
        $"font-family=\"ui-sans-serif, system-ui, sans-serif\"><defs><style>" +
        ".title{font-size:14px;font-weight:700;fill:#222}.label{font-size:11px;fill:#222}" +
        ".small{font-size:9.5px;fill:#444}.diag{font-size:9px;fill:#a1221f}" +
        ".section{stroke-width:1}.boundary-unconf{stroke:#7a1f1f;stroke-width:2.5;stroke-dasharray:6 3}" +
        ".boundary-gap{stroke:#1f4d7a;stroke-width:2.5;stroke-dasharray:2 3}" +
        ".boundary-dup{stroke:#7a5a1f;stroke-width:2}.boundary-inv{stroke:#5b2a86;stroke-width:2}" +
        ".eventbed{stroke-width:0.8}.grid{stroke:#d4dae0;stroke-width:0.6}" +
        "</style></defs>";

    public static string Foot() => "</svg>";

    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
