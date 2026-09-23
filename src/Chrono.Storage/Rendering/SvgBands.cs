using System.Globalization;
using System.Text;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.Rendering;

public static class SvgBands
{
    public static string Render(ReviewModel model)
    {
        var sb = new StringBuilder();
        int width = 980, height = 720;
        int left = 60, top = 50, plotH = 430, plotW = 480;

        sb.Append(Svg.Head(width, height, "svg-bands-rates"));
        sb.Append($"<text x=\"{left}\" y=\"26\" class=\"title\">Uncertainty bands and sedimentation-rate intervals</text>");

        var allBand = model.Candidates.SelectMany(c => c.Band).ToList();
        if (allBand.Count == 0)
        {
            sb.Append($"<text x=\"{left}\" y=\"60\" class=\"diag\">no candidate models: build status {Svg.Escape(model.BuildStatus)}</text>");
            sb.Append(Svg.Foot());
            return sb.ToString();
        }

        double dTop = allBand.Min(b => b.DepthMeters);
        double dBottom = allBand.Max(b => b.DepthMeters);
        double aMin = allBand.Min(b => b.Q025) - 200;
        double aMax = allBand.Max(b => b.Q975) + 200;
        double Y(double d) => top + (d - dTop) / (dBottom - dTop) * plotH;
        double X(double age) => left + (age - aMin) / (aMax - aMin) * plotW;

        for (int g = 0; g <= 5; g++)
        {
            double age = aMin + (aMax - aMin) * g / 5;
            double x = X(age);
            sb.Append($"<line x1=\"{x.ToString(CultureInfo.InvariantCulture)}\" y1=\"{top}\" " +
                      $"x2=\"{x.ToString(CultureInfo.InvariantCulture)}\" y2=\"{top + plotH}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{x.ToString(CultureInfo.InvariantCulture)}\" y=\"{top + plotH + 16}\" " +
                      $"text-anchor=\"middle\" class=\"small\">{age:0}</text>");
        }
        sb.Append($"<text x=\"{left + plotW / 2}\" y=\"{top + plotH + 34}\" text-anchor=\"middle\" class=\"small\">" +
                  $"age (cal BP; {Svg.Escape(model.TimeDatum)})</text>");

        var colors = new[] { "#2a6f97", "#7a5a1f", "#5b2a86", "#1f7a4d" };
        var selected = model.Candidates
            .GroupBy(c => (c.ChainId, c.Family))
            .Select(g => g.OrderBy(c => c.Rank).First())
            .OrderBy(c => c.ChainId).ThenBy(c => c.Family)
            .Take(4)
            .ToList();

        for (int ci = 0; ci < selected.Count; ci++)
        {
            var c = selected[ci];
            string color = colors[ci % colors.Length];
            var upper = string.Join(" ", c.Band
                .Select(b => $"{X(b.Q025).ToString(CultureInfo.InvariantCulture)},{Y(b.DepthMeters).ToString(CultureInfo.InvariantCulture)}"));
            var lower = string.Join(" ", c.Band
                .Select(b => $"{X(b.Q975).ToString(CultureInfo.InvariantCulture)},{Y(b.DepthMeters).ToString(CultureInfo.InvariantCulture)}"));
            sb.Append($"<polygon id=\"band95-{c.Id}\" data-stable-id=\"band95-{c.Id}\" " +
                      $"points=\"{upper} {lower}\" fill=\"{color}\" fill-opacity=\"0.16\" stroke=\"none\"/>");
            var median = string.Join(" ", c.Band
                .Select(b => $"{X(b.Q500).ToString(CultureInfo.InvariantCulture)},{Y(b.DepthMeters).ToString(CultureInfo.InvariantCulture)}"));
            sb.Append($"<polyline id=\"median-{c.Id}\" data-stable-id=\"median-{c.Id}\" " +
                      $"points=\"{median}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"/>");
            sb.Append($"<text x=\"{left + 6}\" y=\"{top + 14 + ci * 14}\" class=\"small\" fill=\"{color}\">" +
                      $"{Svg.Escape(c.ChainId)} / {c.Family} rank {c.Rank}{(c.Tied ? " (tied)" : "")} score {c.Score:0.00} coverage {c.CoveragePct:0}%</text>");
        }

        foreach (var a in model.Anchors.Where(a => !a.Excluded))
        {
            double y = Y(Math.Clamp(a.DepthMeters, dTop, dBottom));
            sb.Append($"<g id=\"band-anchor-{a.Id}\" data-stable-id=\"band-anchor-{a.Id}\">" +
                      $"<line x1=\"{X(a.Q025).ToString(CultureInfo.InvariantCulture)}\" y1=\"{y}\" " +
                      $"x2=\"{X(a.Q975).ToString(CultureInfo.InvariantCulture)}\" y2=\"{y}\" stroke=\"#111\" stroke-width=\"1.2\"/>" +
                      $"<circle cx=\"{X(a.Q500).ToString(CultureInfo.InvariantCulture)}\" cy=\"{y}\" r=\"2.6\" fill=\"#111\"/>" +
                      $"</g>");
        }

        sb.Append($"<text transform=\"rotate(-90)\" x=\"{-(top + plotH / 2)}\" y=\"16\" text-anchor=\"middle\" class=\"small\">" +
                  $"depth ({Svg.Escape(model.VerticalDatum)})</text>");

        RenderRates(sb, model, selected, 580, top, plotH);
        sb.Append(Svg.Foot());
        return sb.ToString();
    }

    private static void RenderRates(StringBuilder sb, ReviewModel model,
        List<ViewCandidate> selected, int x0, int top, int plotH)
    {
        var allRates = selected.SelectMany(c => c.Rates).ToList();
        if (allRates.Count == 0) return;
        double rMax = allRates.Max(r => Math.Min(r.UpperCmPerKa, 200));
        int rowH = Math.Max(14, plotH / allRates.Count);
        sb.Append($"<text x=\"{x0}\" y=\"{top - 12}\" class=\"title\">Rate intervals (cm/ka, 5–95%)</text>");
        int i = 0;
        foreach (var c in selected)
        {
            foreach (var r in c.Rates)
            {
                double y = top + i * rowH + 8;
                double x1 = x0;
                double x2 = x0 + 300;
                double lo = Math.Min(r.LowerCmPerKa, rMax) / rMax * 300;
                double hi = Math.Min(r.UpperCmPerKa, rMax) / rMax * 300;
                sb.Append($"<line x1=\"{x1}\" y1=\"{y}\" x2=\"{x2}\" y2=\"{y}\" stroke=\"#e3e7eb\"/>");
                sb.Append($"<line id=\"{r.Id}\" data-stable-id=\"{r.Id}\" x1=\"{x1 + lo}\" y1=\"{y - 4}\" " +
                          $"x2=\"{x1 + hi}\" y2=\"{y - 4}\" stroke=\"#2a6f97\" stroke-width=\"4\"/>");
                sb.Append($"<text x=\"{x0 + 308}\" y=\"{y}\" class=\"small\">" +
                          $"{r.LowerCmPerKa:0.#}–{Math.Min(r.UpperCmPerKa, double.MaxValue):0.#} ({r.CandidateId})</text>");
                i++;
            }
        }
        sb.Append($"<text x=\"{x0}\" y=\"{top + plotH + 18}\" class=\"small\">" +
                  $"rate cap enforced per parameters version; infinite upper bounds are clipped for display and annotated</text>");
    }
}
