using System.Globalization;
using System.Text;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.Rendering;

public static class SvgAnchors
{
    public static string Render(ReviewModel model)
    {
        var sb = new StringBuilder();
        int width = 760, rowH = 30, marginTop = 44;
        int height = marginTop + model.Anchors.Count * rowH + 60;
        var active = model.Anchors.Where(a => !a.Excluded).ToList();
        double minAge = active.Min(a => Math.Max(0, a.Q025 - 100));
        double maxAge = active.Max(a => a.Q975 + 100);
        int x0 = 150, x1 = width - 40;
        double X(double age) => x0 + (age - minAge) / (maxAge - minAge) * (x1 - x0);

        sb.Append(Svg.Head(width, height, "svg-anchor-distributions"));
        sb.Append($"<text id=\"anchor-title\" x=\"20\" y=\"24\" class=\"title\">" +
                  $"Anchor age distributions (quantiles 2.5–97.5%; no mean substitution)</text>");

        sb.Append($"<text x=\"{x0}\" y=\"{height - 26}\" class=\"small\">{minAge:0} cal BP</text>");
        sb.Append($"<text x=\"{x1}\" y=\"{height - 26}\" text-anchor=\"end\" class=\"small\">{maxAge:0} cal BP</text>");
        sb.Append($"<line x1=\"{x0}\" y1=\"{height - 32}\" x2=\"{x1}\" y2=\"{height - 32}\" stroke=\"#555\"/>");

        for (int i = 0; i < model.Anchors.Count; i++)
        {
            var a = model.Anchors[i];
            double y = marginTop + i * rowH + 10;
            sb.Append($"<g id=\"anchor-row-{a.Id}\" data-stable-id=\"anchor-row-{a.Id}\">");
            sb.Append($"<text x=\"14\" y=\"{y + 4}\" class=\"small\">{Svg.Escape(a.Id)}</text>");
            sb.Append($"<text x=\"14\" y=\"{y + 15}\" class=\"small\" opacity=\"0.7\">{a.DepthMeters:0.00} m</text>");

            if (a.Excluded)
            {
                sb.Append($"<text x=\"{x0}\" y=\"{y + 4}\" class=\"diag\">" +
                          $"EXCLUDED retained: {Svg.Escape(a.ExclusionReason ?? "")}</text>");
            }
            else
            {
                string fill = a.LongTail ? "#b0651f" : "#2a6f97";
                sb.Append($"<line id=\"anchor-range-{a.Id}\" x1=\"{X(a.Q025).ToString(CultureInfo.InvariantCulture)}\" " +
                          $"y1=\"{y}\" x2=\"{X(a.Q975).ToString(CultureInfo.InvariantCulture)}\" y2=\"{y}\" " +
                          $"stroke=\"{fill}\" stroke-width=\"3\"/>");
                sb.Append($"<line x1=\"{X(a.Q025).ToString(CultureInfo.InvariantCulture)}\" y1=\"{y - 5}\" " +
                          $"x2=\"{X(a.Q025).ToString(CultureInfo.InvariantCulture)}\" y2=\"{y + 5}\" stroke=\"{fill}\"/>");
                sb.Append($"<line x1=\"{X(a.Q975).ToString(CultureInfo.InvariantCulture)}\" y1=\"{y - 5}\" " +
                          $"x2=\"{X(a.Q975).ToString(CultureInfo.InvariantCulture)}\" y2=\"{y + 5}\" stroke=\"{fill}\"/>");
                sb.Append($"<circle id=\"anchor-median-{a.Id}\" cx=\"{X(a.Q500).ToString(CultureInfo.InvariantCulture)}\" " +
                          $"cy=\"{y}\" r=\"3.5\" fill=\"#111\"/>");
                string extra = a.LongTail ? " long-tail" : "";
                sb.Append($"<text x=\"{x1 - 4}\" y=\"{y + 4}\" text-anchor=\"end\" class=\"small\">" +
                          $"{Svg.Escape(a.DistributionType)}{extra}</text>");
            }
            if (a.TieGroupId != null)
                sb.Append($"<text x=\"{x0 + 6}\" y=\"{y - 6}\" class=\"diag\">tie group {Svg.Escape(a.TieGroupId)}</text>");
            if (a.DuplicateGroupId != null)
                sb.Append($"<text x=\"{x0 + 6}\" y=\"{y - 6}\" class=\"diag\">duplicate group {Svg.Escape(a.DuplicateGroupId)}</text>");
            sb.Append("</g>");
        }
        sb.Append(Svg.Foot());
        return sb.ToString();
    }
}
