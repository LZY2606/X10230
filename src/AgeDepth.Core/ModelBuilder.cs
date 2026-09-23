using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Core;

internal sealed record NormAnchor(string Id, double Depth, double[] Ages, double[] QuantilePs,
    double[] Penalties, string RawDepth, string LabCode);

internal sealed record NormSegment(CoreSegment Segment, double Top, double Bottom);

internal sealed record Partial(double Score, List<(string AnchorId, double Depth, double Age, double P)> Path);

/// <summary>
/// Pure, deterministic age-depth model engine. No I/O: both the web service and
/// the tests call it directly. Determinism guarantees equivalent inputs produce
/// stable candidate identities and ordering.
/// </summary>
public static partial class ModelBuilder
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ModelResult Build(ModelInput input, long producedAtSeq)
    {
        var diagnostics = new List<Diagnostic>();
        var parents = new List<string> { input.Datum.Id };
        var p = input.Params;

        var segments = NormalizeSegments(input, diagnostics, parents);
        var anchors = NormalizeAnchors(input, diagnostics, parents);
        var sections = BuildSections(segments, anchors, input, diagnostics);

        var failure = FailureKind.None;
        var candidates = new List<Candidate>();
        var rates = new List<RateInterval>();
        var band = new List<BandPoint>();
        var sectionResults = new List<SectionResult>();

        foreach (var section in sections)
        {
            var active = section.Anchors.Where(a => !input.ExcludedAnchorIds.Contains(a.Id)).ToList();
            var sr = new SectionResult(section.Id, section.Top, section.Bottom,
                section.TopBoundary, section.BottomBoundary, active.Select(a => a.Id).ToList(),
                active.Count >= 2);
            sectionResults.Add(sr);

            if (active.Count < 2)
            {
                if (active.Count == 1)
                    diagnostics.Add(Diagnostic.New("SECTION_UNDERCONSTRAINED",
                        DiagnosticSeverity.Info, section.Id, "anchors",
                        "该连续段只有一个有效锚点，无法独立约束沉积速率。",
                        active[0].Depth, RuleIds.KeepEqualGood));
                continue;
            }

            var (sectionCandidates, sectionFailure, sectionDiags) =
                EnumerateCandidates(section, active, p);
            diagnostics.AddRange(sectionDiags);
            if (sectionFailure == FailureKind.BudgetExhausted) failure = FailureKind.BudgetExhausted;

            if (sectionCandidates.Count == 0)
            {
                sr = sr with { Solvable = false };
                sectionResults[^1] = sr;
                failure = FailureKind.SemanticUnsolvable;
                diagnostics.Add(Diagnostic.New("NO_MONOTONIC_PATH",
                    DiagnosticSeverity.Error, section.Id, "anchors",
                    "不存在随深度单调不减的合法年龄路径（规则仅在不断层段内成立）。",
                    section.Top, RuleIds.MonotonicWithinSection));
                continue;
            }

            var best = sectionCandidates.Min(c => c.Score);
            var equalGood = sectionCandidates
                .Where(c => c.Score - best <= p.EqualGoodTolerance)
                .OrderBy(c => c.Score)
                .ThenBy(c => string.Join(",", c.Path.Select(pt => pt.AnchorId + ":" + pt.P.ToString("0.00"))),
                    StringComparer.Ordinal)
                .ToList();
            if (equalGood.Count > 1)
                diagnostics.Add(Diagnostic.New("EQUAL_GOOD_CANDIDATES",
                    DiagnosticSeverity.Info, section.Id, "candidates",
                    "存在 " + equalGood.Count + " 个等优候选，全部保留，不擅自挑选单一模型。",
                    section.Top, RuleIds.KeepEqualGood));

            for (var i = 0; i < equalGood.Count; i++)
            {
                var source = equalGood[i];
                var path = source.Path.Select(pt =>
                    new AnchorPoint(pt.AnchorId, pt.Depth, pt.Age, pt.P)).ToList();
                var id = StableCandidateId(section.Id, i, path);
                candidates.Add(new Candidate(id, i + 1, source.Score, true, section.Id, path));
            }

            rates.AddRange(BuildRates(section.Id, equalGood));
            band.AddRange(BuildBand(section.Id, section, equalGood));
        }

        var inputHash = Digest(input);
        var id = "run_" + inputHash[..16];
        return new ModelResult(id, failure,
            diagnostics.OrderBy(d => d.Position).ThenBy(d => d.Code, StringComparer.Ordinal).ToList(),
            sectionResults, candidates, rates, band, inputHash, p.Version,
            parents.OrderBy(x => x, StringComparer.Ordinal).ToList(), producedAtSeq);
    }

    private static List<NormSegment> NormalizeSegments(ModelInput input, List<Diagnostic> diags, List<string> parents)
    {
        var list = new List<NormSegment>();
        foreach (var seg in input.Segments)
        {
            parents.Add(seg.Id);
            if (!Units.TryDepthMeters(seg.Top, out var top, out var err1))
            {
                diags.Add(Diagnostic.New("DEPTH_NOT_CONVERTIBLE", DiagnosticSeverity.Error,
                    seg.Id, "top", err1!, 0));
                continue;
            }
            if (!Units.TryDepthMeters(seg.Bottom, out var bottom, out var err2))
            {
                diags.Add(Diagnostic.New("DEPTH_NOT_CONVERTIBLE", DiagnosticSeverity.Error,
                    seg.Id, "bottom", err2!, top));
                continue;
            }
            if (bottom <= top)
                diags.Add(Diagnostic.New("SEGMENT_ORDER_INVALID", DiagnosticSeverity.Error,
                    seg.Id, "bottom", "分段底界不深于顶界，保留原始表示。", top));
            list.Add(new NormSegment(seg, top, bottom));
        }
        return list.OrderBy(s => s.Top).ThenBy(s => s.Segment.Id, StringComparer.Ordinal).ToList();
    }

    private static List<NormAnchor> NormalizeAnchors(ModelInput input, List<Diagnostic> diags, List<string> parents)
    {
        var list = new List<NormAnchor>();
        foreach (var anchor in input.Anchors)
        {
            parents.Add(anchor.Id);
            if (!Units.TryDepthMeters(anchor.Depth, out var depth, out var errD))
            {
                diags.Add(Diagnostic.New("DEPTH_NOT_CONVERTIBLE", DiagnosticSeverity.Error,
                    anchor.Id, "depth", errD!, 0));
                continue;
            }
            var ages = new List<double>();
            var qs = new List<double>();
            var penalties = new List<double>();
            double? offsetYears = null;
            if (anchor.ReservoirOffset is { } ro)
            {
                parents.Add(anchor.Id + ":reservoir");
                if (Units.TryAgeCalBp(ro, out var off, out var errR))
                    offsetYears = off;
                else
                    diags.Add(Diagnostic.New("RESERVOIR_OFFSET_NOT_CONVERTIBLE",
                        DiagnosticSeverity.Warning, anchor.Id, "reservoirOffset", errR!, depth));
            }
            var k = input.Params.SamplesPerAnchor;
            for (var i = 0; i < k; i++)
            {
                var pp = (i + 0.5) / k;
                var rawValue = anchor.Distribution.Quantile(pp);
                qs.Add(pp);
                ages.Add(rawValue - (offsetYears ?? 0));
                penalties.Add(anchor.Distribution.Penalty(pp, rawValue));
            }
            if (ages.Count >= 2 && ages.Max() - ages.Min() > 5000)
                diags.Add(Diagnostic.New("AGE_DISTRIBUTION_LONG_TAIL", DiagnosticSeverity.Info,
                    anchor.Id, "distribution",
                    "锚点年龄分布呈长尾，完整保留分布，不以均值替代。", depth,
                    RuleIds.DistributionNotMean));
            list.Add(new NormAnchor(anchor.Id, depth, ages.ToArray(), qs.ToArray(),
                penalties.ToArray(), anchor.Depth.Raw, anchor.LabCode));
        }
        return list.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }
