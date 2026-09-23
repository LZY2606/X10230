namespace AgeDepth.Core;

/// <summary>One entry of the counterexample archive: baseline perturbed by exactly one source.</summary>
public sealed record ArchiveEntry(
    string Id, string Title, string PerturbedSource, string ExpectedScope,
    IReadOnlyList<string> AffectedSegments, bool ScopeOk,
    IReadOnlyList<string> AdoptedCandidates, IReadOnlyList<string> RejectedCandidates,
    IReadOnlyList<Diagnostic> KeyDiagnostics, string BaselineDigest, string VariantDigest);

/// <summary>
/// Builds the counterexample archive: a baseline model, then variants that each change
/// exactly one direct source (duplicate depth, unconformity, inverted section, co-located
/// anchors, long-tail distribution, local recompute). Adopted and rejected results are
/// both preserved per entry. The concurrent-freeze entry is exercised at the store layer.
/// </summary>
public static class AdversarialArchive
{
    public static ModelInput BaselineInput() => new(
        Anchors: new[]
        {
            new Anchor("BL-A1", "CORE-Z", Quantity.CoreDepth(1.0), AgeDistribution.Normal(2.0, 0.15), "1.0m|N(2.0,0.15)", "lab-c14"),
            new Anchor("BL-A2", "CORE-Z", Quantity.CoreDepth(2.0), AgeDistribution.Normal(4.1, 0.20), "2.0m|N(4.1,0.20)", "lab-c14"),
            new Anchor("BL-A3", "CORE-Z", Quantity.CoreDepth(3.5), AgeDistribution.Normal(7.5, 0.30), "3.5m|N(7.5,0.30)", "lab-c14"),
            new Anchor("BL-A4", "CORE-Z", Quantity.CoreDepth(5.0), AgeDistribution.Normal(11.0, 0.40), "5.0m|N(11.0,0.40)", "lab-c14"),
        },
        Boundaries: Array.Empty<Boundary>(),
        CoreTopM: 0, CoreBottomM: 6,
        ExcludedAnchorIds: new HashSet<string>(),
        ReservoirOffsetsBySource: new Dictionary<string, double>());

    public static IReadOnlyList<ArchiveEntry> Build(ModelParams? p = null)
    {
        p ??= new ModelParams();
        var baseline = ModelBuilder.Build(BaselineInput(), p);
        var entries = new List<ArchiveEntry>();

        void Variant(string id, string title, string source, string expectedScope, ModelInput input)
        {
            var result = ModelBuilder.Build(input, p);
            var affected = Affected(baseline, result);
            var scopeOk = result.Status != ResultStatus.Ok || affected.All(a => expectedScope.Split(';').Contains(a));
            entries.Add(new ArchiveEntry(id, title, source, expectedScope,
                affected, scopeOk,
                result.Segments.SelectMany(s => s.Candidates).Where(c => c.Status == "adopted").Select(c => c.Id).ToArray(),
                result.Segments.SelectMany(s => s.Candidates).Where(c => c.Status == "rejected").Select(c => c.Id + ":" + c.Reason).ToArray(),
                result.Diagnostics.Concat(result.Segments.SelectMany(s => s.Diagnostics))
                    .Where(d => d.Severity is "error" or "warning" || d.Code is "CoLocatedAnchors").ToArray(),
                baseline.InputDigest, result.InputDigest));
        }

        var b = BaselineInput();

        // 1. duplicate depth: new anchor at an existing anchor depth
        Variant("dup-depth", "深度重复", "anchor:BL-A2", "seg-0",
            b with { Anchors = b.Anchors.Concat(new[]{ new Anchor("BL-A2B", "CORE-Z", Quantity.CoreDepth(2.0), AgeDistribution.Normal(4.3, 0.2), "2.0m|N(4.3,0.2)", "lab-c14") }).ToArray() });

        // 2. unconformity: explicit boundary splits the section; age may jump across
        Variant("unconformity", "不整合", "boundary:U1", "seg-0;seg-1",
            b with { Boundaries = new[]{ new Boundary("U1", "CORE-Z", 3.5, 3.5, "unconformity") } });

        // 3. inverted section: younger anchor below older one in a continuous segment
        Variant("inverted", "倒置层段", "anchor:BL-INV", "seg-0",
            b with { Anchors = b.Anchors.Concat(new[]{ new Anchor("BL-INV", "CORE-Z", Quantity.CoreDepth(4.5), AgeDistribution.Normal(3.0, 0.2), "4.5m|N(3.0,0.2)", "lab-c14") }).ToArray() });

        // 4. co-located anchors: two anchors at exactly the same depth
        Variant("co-located", "并列锚点", "anchor:BL-A3", "seg-0",
            b with { Anchors = b.Anchors.Concat(new[]{ new Anchor("BL-A3B", "CORE-Z", Quantity.CoreDepth(3.5), AgeDistribution.Normal(7.9, 0.25), "3.5m|N(7.9,0.25)", "lab-c14") }).ToArray() });

        // 5. long-tail distribution: samples with a heavy old tail; median must lead, not mean
        Variant("long-tail", "年龄分布长尾", "anchor:BL-LT", "seg-0",
            b with { Anchors = b.Anchors.Concat(new[]{ new Anchor("BL-LT", "CORE-Z", Quantity.CoreDepth(2.8),
                AgeDistribution.Samples(new[]{ 5.0, 5.1, 5.2, 5.2, 5.3, 5.3, 5.4, 5.5, 6.0, 7.5, 9.0, 12.0 }), "2.8m|samples(12)", "lab-c14") }).ToArray() });

        // 6. local recompute: shift one anchor's depth; only its segment may change
        Variant("local-recompute", "局部重算", "anchor:BL-A2", "seg-0",
            b with { Anchors = b.Anchors.Select(a => a.Id == "BL-A2" ? a with { Depth = Quantity.CoreDepth(2.2) } : a).ToArray() });

        return entries;
    }

    public static IReadOnlyList<string> Affected(ModelResult baseline, ModelResult variant)
    {
        var baseHash = baseline.Segments.ToDictionary(s => s.SegmentId, s => s.ResultHash);
        var varHash = variant.Segments.ToDictionary(s => s.SegmentId, s => s.ResultHash);
        return baseHash.Keys.Union(varHash.Keys)
            .Where(k => !baseHash.TryGetValue(k, out var h1) || !varHash.TryGetValue(k, out var h2) || h1 != h2)
            .OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }
}
