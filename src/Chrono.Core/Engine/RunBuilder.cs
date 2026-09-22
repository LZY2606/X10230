using Chrono.Core.Model;
using Chrono.Core.Sourcing;

namespace Chrono.Core.Engine;

public sealed record RunBuildResult(
    IReadOnlyList<RunSpec> Runs,
    IReadOnlyList<PreparedAnchor> Prepared,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record RunSpec(
    string Id,
    double TopMetres,
    double BottomMetres,
    bool BoundedByGap,
    List<string> BoundaryEventIds,
    List<PreparedAnchor> Anchors);

/// <summary>
/// Builds continuous runs from segments. Gaps and unconformities are explicit boundaries: no
/// interpolation or monotonicity ever crosses them. Anchors are located to the segment containing
/// their depth; duplicate and inverted situations are reported structurally.
/// </summary>
public static class RunBuilder
{
    public static RunBuildResult Build(ProjectState state)
    {
        var diags = new List<Diagnostic>();
        var boundaries = new List<BoundaryInfo>();

        // Prepared anchors: reservoir correction is derived here; raw distribution kept.
        var prepared = new List<PreparedAnchor>();
        foreach (var a in state.ActiveAnchors().OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (a.Excluded) continue;
            if (!a.DepthMetres.HasValue || a.Distribution is null || a.UnconvertibleReason is not null)
            {
                diags.Add(new Diagnostic("anchor.unusable", Severity.Warning, $"anchor:{a.Id}",
                    $"anchor retained but excluded from derivation ({a.UnconvertibleReason ?? "incomplete"})",
                    new Dictionary<string, string> { ["rawAge"] = a.RawAge, ["rawDepth"] = a.RawDepth }));
                continue;
            }
            AgeDistribution cal = a.Distribution;
            string? note = null;
            if (state.Parameters.ApplyReservoir && a.ReservoirOffsetId is not null
                && state.Reservoirs.TryGetValue(a.ReservoirOffsetId, out var res)
                && a.Distribution is GaussianAge g)
            {
                var mean = g.MeanKa - res.MeanKa;
                var sigma = Math.Sqrt(g.SigmaKa * g.SigmaKa + res.SigmaKa * res.SigmaKa);
                cal = new ShiftedGaussianAge(mean, sigma, $"{g.RawText} | -R({res.ReferenceCurve})");
                note = $"reservoir {res.ReferenceCurve}: -{res.MeanKa:R}±{res.SigmaKa:R} ka";
            }
            else if (a.ReservoirOffsetId is not null && a.Distribution is not GaussianAge)
            {
                note = $"reservoir offset '{a.ReservoirOffsetId}' not convolved (non-gaussian age); raw distribution retained";
            }
            prepared.Add(new PreparedAnchor(a, cal, a.Distribution, a.DepthMetres.Value, note));
        }

        var segments = state.Segments.Values.OrderBy(s => s.TopMetres).ToList();
        var intervals = new List<Interval>();
        foreach (var s in segments)
        {
            if (s.BottomMetres < s.TopMetres)
                diags.Add(new Diagnostic("segment.inverted", Severity.Error, $"segment:{s.Id}",
                    $"segment bottom {s.RawBottom} above top {s.RawTop}"));
            intervals.Add(new Interval(s.Id, s.TopMetres, s.BottomMetres, s.LowerContact, s.Code));
        }

        // explicit human-added boundaries (inversion resolved into an edge)
        foreach (var b in state.AddedBoundaries.OrderBy(b => b.AtDepthMetres))
            boundaries.Add(new BoundaryInfo(b.AtDepthMetres, b.Contact, "decision:" + b.EventId, b.Justification));

        // material contacts between successive segments
        for (int i = 0; i < intervals.Count - 1; i++)
        {
            var contact = intervals[i].Contact;
            if (contact != ContactKind.Conformable)
            {
                boundaries.Add(new BoundaryInfo(intervals[i].Bottom, contact,
                    $"segment:{intervals[i].Id}.lowerContact", intervals[i].Code));
            }
            else if (intervals[i + 1].Top > intervals[i].Bottom + 1e-9)
            {
                // physical space between conformable segments is itself an explicit recovery gap
                boundaries.Add(new BoundaryInfo(intervals[i].Bottom, ContactKind.RecoveryGap,
                    $"segment:{intervals[i].Id}-{intervals[i+1].Id}.space", "physical gap between mapped segments"));
            }
        }
        boundaries = boundaries.DistinctBy(b => (b.AtDepth, b.Kind, b.Source)).OrderBy(b => b.AtDepth).ToList();

        double top = segments.Count == 0 ? prepared.DefaultIfEmpty()!.First().DepthMetres : segments[0].TopMetres;
        double bottom = segments.Count == 0 ? prepared.Max(p => p.DepthMetres) : segments[^1].BottomMetres;

        // split covered range at boundaries into runs
        var specs = new List<RunSpec>();
        var cuts = new List<double> { top };
        cuts.AddRange(boundaries.Select(b => b.AtDepth).Where(d => d > top + 1e-9 && d < bottom - 1e-9));
        cuts.Add(bottom);
        cuts = cuts.Distinct().OrderBy(d => d).ToList();

        for (int i = 0; i < cuts.Count - 1; i++)
        {
            double rTop = cuts[i], rBottom = cuts[i + 1];
            var anchors = prepared
                .Where(p => p.DepthMetres >= rTop - 1e-9 && p.DepthMetres <= rBottom + 1e-9)
                .ToList();
            bool bounded = boundaries.Any(b => Math.Abs(b.AtDepth - rTop) < 1e-9 || Math.Abs(b.AtDepth - rBottom) < 1e-9);
            var bevs = boundaries
                .Where(b => Math.Abs(b.AtDepth - rTop) < 1e-9 || Math.Abs(b.AtDepth - rBottom) < 1e-9)
                .Select(b => b.Source).ToList();

            // apply duplicate-depth human resolutions
            foreach (var grp in anchors.GroupBy(p => Round(p.DepthMetres)).Where(g => g.Count() > 1).ToList())
            {
                var res = state.DuplicateResolutions.LastOrDefault(r =>
                    Math.Abs(r.DepthMetres - grp.Key) < 1e-9);
                if (res is null) continue; // unresolved -> engine flags semantic block
                if (res.Mode == "drop" && res.DroppedAnchorId is not null)
                    anchors.RemoveAll(p => p.Anchor.Id == res.DroppedAnchorId);
            }

            string id = RunId(rTop, rBottom);
            specs.Add(new RunSpec(id, rTop, rBottom, bounded, bevs, anchors));
        }

        // anchors outside every segment (orphans) form their own run flagged for review
        foreach (var p in prepared)
        {
            if (!specs.Any(sp => p.DepthMetres >= sp.TopMetres - 1e-9 && p.DepthMetres <= sp.BottomMetres + 1e-9))
            {
                diags.Add(new Diagnostic("anchor.orphan", Severity.Warning, $"anchor:{p.Anchor.Id}",
                    "anchor depth lies outside every mapped segment"));
            }
        }

        return new RunBuildResult(specs, prepared, diags);
    }

    public static string RunId(double top, double bottom)
        => "run_" + StableId.Sha1("run", $"{top:R}|{bottom:R}")[..18];

    private static double Round(double d) => Math.Round(d, 9);

    private sealed record Interval(string Id, double Top, double Bottom, ContactKind Contact, string Code);
    private sealed record BoundaryInfo(double AtDepth, ContactKind Kind, string Source, string Justification);
}
