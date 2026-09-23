using System.Text;
using System.Text.Json;

namespace AgeDepth.Core;

internal sealed record Section(
    string Id,
    double Top,
    double Bottom,
    string TopBoundary,
    string BottomBoundary,
    List<NormAnchor> Anchors);

public static partial class ModelBuilder
{
    private static List<Section> BuildSections(
        List<NormSegment> segments, List<NormAnchor> anchors, ModelInput input, List<Diagnostic> diags)
    {
        var eps = input.Params.DepthEpsilonMeters;
        var sections = new List<Section>();
        var boundaries = new List<(double at, string label)>();

        if (segments.Count == 0)
        {
            sections.Add(new Section("sec-00",
                anchors.Count > 0 ? anchors.Min(a => a.Depth) : 0,
                anchors.Count > 0 ? anchors.Max(a => a.Depth) : 0,
                "core-top", "core-bottom", anchors.ToList()));
            return sections;
        }

        var cursor = segments[0].Top;
        var topBoundary = "core-top";
        var inSection = new List<NormSegment>();
        var sectionIndex = 0;

        void Emit(List<NormSegment> body, double top, string topLabel, string bottomLabel)
        {
            var bottom = body.Count == 0 ? top : body.Max(s => s.Bottom);
            var id = "sec-" + sectionIndex.ToString("00");
            var picked = anchors.Where(a => a.Depth + eps >= top - eps && a.Depth <= bottom + eps).ToList();
            sections.Add(new Section(id, top, bottom, topLabel, bottomLabel, picked));
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.Kind == SegmentKind.Continuous)
            {
                inSection.Add(seg);
                continue;
            }

            // A gap or unconformity is an explicit boundary: monotonicity never crosses it.
            if (inSection.Count > 0)
            {
                var top = inSection.Min(s => s.Top);
                var bottom = inSection.Max(s => s.Bottom);
                var label = seg.Kind == SegmentKind.Unconformity
                    ? "unconformity:" + seg.Segment.Id
                    : "gap:" + seg.Segment.Id;
                Emit(inSection, top, topBoundary, label);
                sectionIndex++;
                topBoundary = label;
                inSection = new List<NormSegment>();
                cursor = seg.Bottom;
            }
            boundaries.Add((seg.Top, seg.Kind == SegmentKind.Unconformity ? "不整合" : "沉积间断"));
            diags.Add(Diagnostic.New(seg.Kind == SegmentKind.Unconformity
                    ? "UNCONFORMITY_BOUNDARY" : "GAP_BOUNDARY",
                DiagnosticSeverity.Info, seg.Segment.Id, "kind",
                (seg.Kind == SegmentKind.Unconformity ? "不整合" : "深度缺口/沉积间断")
                + "作为显式边界，单调性不在其两侧连续传递。",
                seg.Top, RuleIds.GapIsBoundary));
        }

        var remaining = inSection.Count > 0;
        if (remaining)
        {
            var top = inSection.Min(s => s.Top);
            Emit(inSection, top, topBoundary, "core-bottom");
        }

        // Anchors not covered by any continuous section sit on a boundary.
        var covered = new HashSet<string>(sections.SelectMany(s => s.Anchors.Select(a => a.Id)));
        foreach (var anchor in anchors.Where(a => !covered.Contains(a.Id)))
            diags.Add(Diagnostic.New("ANCHOR_ON_BOUNDARY", DiagnosticSeverity.Warning,
                anchor.Id, "depth",
                "锚点落在缺口/不整合边界上，不属于任何不断层段，不参与单调性约束。",
                anchor.Depth, RuleIds.GapIsBoundary));

        DetectDepthRelations(sections, eps, diags);
        return sections;
    }

    private static void DetectDepthRelations(List<Section> sections, double eps, List<Diagnostic> diags)
    {
        foreach (var section in sections)
        {
            var ordered = section.Anchors;
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    if (Math.Abs(ordered[i].Depth - ordered[j].Depth) <= eps)
                        diags.Add(Diagnostic.New("DEPTH_DUPLICATE_TIED_ANCHORS",
                            DiagnosticSeverity.Warning, ordered[j].Id, "depth",
                            "深度重复/并列锚点: " + ordered[i].Id + " 与 " + ordered[j].Id
                            + " 位于同一深度，保留全部排列作为候选。",
                            ordered[j].Depth, RuleIds.KeepEqualGood));
                }
            }
            for (var i = 1; i < ordered.Count; i++)
            {
                var upper = ordered[i - 1];
                var lower = ordered[i];
                var upperMedian = Statistics.Percentile(
                    upper.Ages.OrderBy(v => v).ToList(), 0.5);
                var lowerMedian = Statistics.Percentile(
                    lower.Ages.OrderBy(v => v).ToList(), 0.5);
                if (lowerMedian + 1e-9 < upperMedian)
                {
                    var hard = lower.Ages.Max() < upper.Ages.Min();
                    diags.Add(Diagnostic.New("INVERTED_AGE_SEQUENCE",
                        hard ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                        lower.Id, "distribution",
                        (hard ? "倒置层段: " : "年龄中位倒置: ")
                        + lower.Id + " 的年龄比上覆锚点 " + upper.Id
                        + (hard ? " 更年轻且分布无重叠。" : " 更年轻，但分布尾部允许单调路径。"),
                        lower.Depth, RuleIds.MonotonicWithinSection));
                }
            }
        }
    }

    private static (List<Partial> partials, FailureKind failure, List<Diagnostic> diags)
        EnumerateCandidates(Section section, List<NormAnchor> active, ModelParams p)
    {
        var diags = new List<Diagnostic>();
        var beam = new List<Partial> { new(0, new List<(string, double, double, double)>()) };

        foreach (var anchor in active)
        {
            var next = new List<Partial>();
            for (var k = 0; k < anchor.Ages.Length; k++)
            {
                var age = anchor.Ages[k];
                var qp = anchor.QuantilePs[k];
                var penalty = active.First(a => a.Id == anchor.Id).Equals(anchor)
                    ? QuantilePenalty(section, anchor, k)
                    : 0;
                foreach (var parent in beam)
                {
                    if (parent.Path.Count > 0)
                    {
                        var last = parent.Path[^1];
                        if (age + 1e-9 < last.Item3) continue; // monotonic non-decreasing age
                        if (Math.Abs(anchor.Depth - last.Item2) <= p.DepthEpsilonMeters
                            && string.CompareOrdinal(anchor.Id, last.Item1) < 0)
                        {
                            // Tied depth: canonicalize tie order by stable anchor id.
                            continue;
                        }
                    }
                    var path = new List<(string, double, double, double)>(parent.Path)
                    {
                        (anchor.Id, anchor.Depth, age, qp)
                    };
                    next.Add(new Partial(parent.Score + penalty, path));
                }
            }

            // Deterministic pruning keeps the best MaxCandidates partial paths.
            next = next.OrderBy(pt => pt.Score)
                .ThenBy(pt => PathKey(pt.Path), StringComparer.Ordinal)
                .ToList();
            if (next.Count > p.MaxCandidates)
            {
                diags.Add(Diagnostic.New("CANDIDATE_BUDGET_EXHAUSTED",
                    DiagnosticSeverity.Error, anchor.Id, "candidates",
                    "合法候选数量超过预算 " + p.MaxCandidates + "，判定为资源预算耗尽。",
                    anchor.Depth, RuleIds.LocalRecompute));
                return (new List<Partial>(), FailureKind.BudgetExhausted, diags);
            }
            beam = next;
        }

        return (beam, FailureKind.None, diags);
    }

    private static double QuantilePenalty(Section section, NormAnchor anchor, int k)
    {
        // Penalize using quantile distance from the median, weighted by age spread.
        var p = anchor.QuantilePs[k];
        var spread = anchor.Ages.Max() - anchor.Ages.Min();
        return Math.Abs(p - 0.5) * 2.0 * (spread > 0 ? 1 : 0);
    }

    private static string PathKey(List<(string AnchorId, double Depth, double Age, double P)> path) =>
        string.Join(">", path.Select(pt => pt.AnchorId + "@" + pt.P.ToString("0.###",
            System.Globalization.CultureInfo.InvariantCulture)));

    private static string StableCandidateId(string sectionId, int rank, List<AnchorPoint> path)
    {
        var key = sectionId + "|" + rank.ToString("00") + "|"
            + string.Join(">", path.Select(pt =>
                pt.AnchorId + ":" + pt.Age.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "cand_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static List<RateInterval> BuildRates(string sectionId, List<Partial> equalGood)
    {
        var result = new List<RateInterval>();
        var primary = equalGood[0].Path;
        for (var i = 1; i < primary.Count; i++)
        {
            var lo = primary[i - 1];
            var hi = primary[i];
            var rates = equalGood
                .Where(c => c.Path.Count > i)
                .Select(c =>
                {
                    var dDepth = c.Path[i].Item2 - c.Path[i - 1].Item2; // meters
                    var dAgeKa = Math.Max((c.Path[i].Item3 - c.Path[i - 1].Item3) / 1000.0, 1e-6);
                    return dDepth / dAgeKa;
                })
                .OrderBy(v => v)
                .ToList();
            result.Add(new RateInterval(
                "rate_" + sectionId + "_" + i.ToString("00"),
                sectionId, lo.Item1, hi.Item1, lo.Item2, hi.Item2,
                Statistics.Percentile(rates, 0.5),
                Statistics.Percentile(rates, 0.05),
                Statistics.Percentile(rates, 0.95)));
        }
        return result;
    }

    private static List<BandPoint> BuildBand(string sectionId, Section section, List<Partial> equalGood)
    {
        var result = new List<BandPoint>();
        const int grid = 24;
        for (var g = 0; g <= grid; g++)
        {
            var depth = section.Top + (section.Bottom - section.Top) * g / grid;
            var ages = new List<double>();
            foreach (var cand in equalGood)
            {
                var age = InterpolateDepth(cand.Path, depth);
                if (age is not null) ages.Add(age.Value);
            }
            if (ages.Count == 0) continue;
            ages.Sort();
            result.Add(new BandPoint(sectionId, depth,
                Statistics.Percentile(ages, 0.05),
                Statistics.Percentile(ages, 0.5),
                Statistics.Percentile(ages, 0.95)));
        }
        return result;
    }

    private static double? InterpolateDepth(
        List<(string AnchorId, double Depth, double Age, double P)> path, double depth)
    {
        if (path.Count == 0 || depth < path[0].Depth || depth > path[^1].Depth) return null;
        for (var i = 1; i < path.Count; i++)
        {
            if (depth <= path[i].Depth)
            {
                var (_, d0, a0, _) = path[i - 1];
                var (_, d1, a1, _) = path[i];
                if (Math.Abs(d1 - d0) < 1e-12) return (a0 + a1) / 2;
                var f = (depth - d0) / (d1 - d0);
                return a0 + (a1 - a0) * f;
            }
        }
        return null;
    }

    private static string Digest(ModelInput input)
    {
        var sb = new StringBuilder();
        sb.Append("v").Append(input.Params.Version)
          .Append(":k=").Append(input.Params.SamplesPerAnchor)
          .Append(":max=").Append(input.Params.MaxCandidates)
          .Append(":tol=").Append(input.Params.EqualGoodTolerance.ToString(
              System.Globalization.CultureInfo.InvariantCulture))
          .Append(":eps=").Append(input.Params.DepthEpsilonMeters.ToString(
              System.Globalization.CultureInfo.InvariantCulture));
        foreach (var seg in input.Segments.OrderBy(s => s.Id, StringComparer.Ordinal))
            sb.Append('|').Append(seg.Id).Append('=').Append(seg.Top.Raw).Append('/')
              .Append(seg.Bottom.Raw).Append(':').Append(seg.Kind);
        foreach (var a in input.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
            sb.Append('|').Append(a.Id).Append('=').Append(a.Depth.Raw).Append('@')
              .Append(a.Distribution.Raw).Append("~").Append(a.ReservoirOffset?.Raw ?? "-");
        sb.Append("|exclude=").Append(string.Join(",",
            input.ExcludedAnchorIds.OrderBy(x => x, StringComparer.Ordinal)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
