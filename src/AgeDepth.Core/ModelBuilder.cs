using System.Text;

namespace AgeDepth.Core;

public sealed record RuleSet(string Version, int MaxCandidates, double DepthToleranceM, double OverlapQuantile);

public sealed record AnchorPoint(
    string MaterialId,
    string Kind,
    string Label,
    double DepthM,
    double AgeLoKa,
    double AgeMedKa,
    double AgeHiKa,
    double AgeMeanKa,
    string DistType,
    string RawDepth,
    string RawAge,
    string? ReservoirOffsetId);

public sealed record RateInterval(
    string UpperAnchorId,
    string LowerAnchorId,
    double DepthTopM,
    double DepthBottomM,
    double? RateMedianMPerKa,
    double? RateLoMPerKa,
    double? RateHiMPerKa,
    string Note);

public sealed record BandPoint(double DepthM, double LoKa, double MedKa, double HiKa);

public sealed record CandidateModel(
    string CandidateId,
    string SegmentId,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<BandPoint> Band,
    IReadOnlyList<RateInterval> Rates,
    double ValidTopM,
    double ValidBottomM,
    string RuleVersion,
    string InputHash,
    IReadOnlyList<string> SourceMaterialIds,
    IReadOnlyList<string> DecisionIds);

public sealed record SegmentResult(
    string SegmentId,
    double TopM,
    double BottomM,
    string? UpperBoundary,
    string? LowerBoundary,
    IReadOnlyList<AnchorPoint> Anchors,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record RecomputeScope(
    IReadOnlyList<string> RecomputedSegments,
    IReadOnlyList<string> ReusedSegments);

public sealed record ComputeResult(
    ComputeStatus Status,
    string RuleVersion,
    string InputHash,
    IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments,
    RecomputeScope? Scope);

/// <summary>
/// Builds multi-candidate depth/age models. Rules live here, on the server
/// side, and are unit-tested directly.
/// </summary>
public sealed class ModelBuilder
{
    private readonly RuleSet _rules;

    public ModelBuilder(RuleSet rules) => _rules = rules;

    public ComputeResult Build(
        IReadOnlyList<Material> allMaterials,
        IReadOnlyList<Decision> decisions,
        IReadOnlySet<string>? changedMaterialIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<CandidateModel>>? previousCandidatesBySegment = null,
        string? previousRuleVersion = null)
    {
        var diagnostics = new List<Diagnostic>();
        var judgments = new List<Judgment>();

        // Current version of each material lineage; superseded records stay in
        // the archive but do not feed the model.
        var superseded = allMaterials.Where(m => m.Supersedes != null)
            .Select(m => m.Supersedes!).ToHashSet();
        var materials = allMaterials.Where(m => !superseded.Contains(m.Id)).ToList();

        var datumOffsets = materials.OfType<DepthDatumMaterial>()
            .ToDictionary(m => m.DatumName, m => m.OffsetToMbsfM);
        var offsets = materials.OfType<ReservoirOffsetMaterial>().ToDictionary(m => m.Id);
        var gaps = ResolveGaps(materials, datumOffsets, diagnostics);
        var anchors = ResolveAnchors(materials, datumOffsets, offsets, decisions, diagnostics, judgments);

        var (topM, bottomM) = ColumnExtent(gaps, anchors);
        var segmentRanges = BuildSegments(topM, bottomM, gaps);
        foreach (var gap in gaps)
        {
            judgments.Add(new Judgment(
                RuleIds.GapIsExplicitBoundary,
                $"gap:{gap.Material.Id}",
                "explicit-boundary",
                $"{gap.Material.GapKind} {gap.TopM:0.###}-{gap.BottomM:0.###} m mbsf; no age interpolation crosses it"));
        }

        var excluded = EffectiveExclusions(decisions);
        var candidates = new List<CandidateModel>();
        var segmentResults = new List<SegmentResult>();
        var recomputed = new List<string>();
        var reused = new List<string>();
        var budgetHit = false;

        foreach (var range in segmentRanges)
        {
            var segId = SegmentId(range.Top, range.Bottom);
            var inSegment = anchors
                .Where(a => a.DepthM > range.Top + 1e-9 && a.DepthM < range.Bottom - 1e-9)
                .OrderBy(a => a.DepthM).ThenBy(a => a.MaterialId)
                .ToList();

            var segDiags = new List<Diagnostic>();
            PlaceAnchors(inSegment, segDiags, excluded);

            var affected = changedMaterialIds == null || previousCandidatesBySegment == null
                ? true
                : rulesChanged(previousRuleVersion) || inSegment.Any(a => changedMaterialIds.Contains(a.MaterialId));

            if (!affected && previousCandidatesBySegment!.TryGetValue(segId, out var cached))
            {
                reused.Add(segId);
                candidates.AddRange(cached);
                segmentResults.Add(new SegmentResult(segId, range.Top, range.Bottom,
                    range.UpperBoundary, range.LowerBoundary,
                    inSegment.Where(a => !excluded.Contains(a.MaterialId)).ToList(), segDiags));
                continue;
            }
            recomputed.Add(segId);

            var segCandidates = BuildSegmentCandidates(
                segId, range, inSegment, excluded, decisions, diagnostics, segDiags, judgments,
                ref budgetHit);
            candidates.AddRange(segCandidates);
            segmentResults.Add(new SegmentResult(segId, range.Top, range.Bottom,
                range.UpperBoundary, range.LowerBoundary,
                inSegment.Where(a => !excluded.Contains(a.MaterialId)).ToList(), segDiags));
        }

        var inputHash = InputHash(allMaterials, decisions);
        candidates = candidates.OrderBy(c => c.SegmentId).ThenBy(c => c.CandidateId).ToList();

        var status = budgetHit ? ComputeStatus.BudgetExhausted
            : candidates.Count == 0 && anchors.Count > 0 ? ComputeStatus.Unsolvable
            : ComputeStatus.Ok;

        return new ComputeResult(status, _rules.Version, inputHash,
            segmentResults, candidates, diagnostics.OrderBy(d => d.Path).ToList(),
            judgments, new RecomputeScope(recomputed, reused));
    }

    private bool rulesChanged(string? previousRuleVersion)
        => previousRuleVersion != _rules.Version;
