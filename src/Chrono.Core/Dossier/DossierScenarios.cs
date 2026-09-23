using System.Collections.Generic;

namespace Chrono.Core.Dossier;

/// <summary>
/// Counter-example archive scenarios. Each changes exactly one direct source from the baseline and
/// records both adopted and non-adopted outcomes. Replay actually passes through the named situation.
/// </summary>
public enum DossierSituation
{
    Baseline = 0,
    DuplicateDepth = 1,
    Unconformity = 2,
    InvertedInterval = 3,
    TiedAnchors = 4,
    LongTailDistribution = 5,
    LocalRecompute = 6,
    ConcurrentFreeze = 7
}

public sealed record DossierScenario(
    DossierSituation Situation,
    string Branch,
    string Title,
    string OneSourceChange,
    string ExpectedContainment,
    IReadOnlyList<string> AcceptedOutcomeIds,
    IReadOnlyList<string> RejectedOutcomeIds,
    string MachineEventJson);

public static class DossierCatalog
{
    public static IReadOnlyList<DossierScenario> All() => new[]
    {
        new DossierScenario(DossierSituation.Baseline, "main",
            "Baseline: datum, gaps, radiometric distributions, reservoir offsets, event ties, exclusions retained",
            "none — full immutable material layer",
            "all subsequent scenarios compare against this fingerprint",
            new[] { "baseline::derivation" }, System.Array.Empty<string>(),
            "{\"situation\":\"baseline\",\"check\":\"IMMUTABLE_LAYER_COMPLETE\"}"),

        new DossierScenario(DossierSituation.DuplicateDepth, "dup-depth",
            "Depth repeat: a second anchor at the same normalized depth with a different age",
            "add one radiometric date at an already-tied depth",
            "impact confined to the containing track; other tracks keep identical fingerprints",
            System.Array.Empty<string>(),
            new[] { "dup-depth::cand[combined]" },
            "{\"situation\":\"duplicate_depth\",\"rule\":\"DEPTH_REPEAT_IS_INTRA_TRACK_CONFLICT\"}"),

        new DossierScenario(DossierSituation.Unconformity, "unconformity",
            "Unconformity: insert one gap boundary",
            "add one gap material; no anchor changed",
            "boundary splits tracks; ages never interpolated or made monotone across it",
            new[] { "unconformity::track[below]" },
            System.Array.Empty<string>(),
            "{\"situation\":\"unconformity\",\"rule\":\"GAP_IS_EXPLICIT_BOUNDARY\"}"),

        new DossierScenario(DossierSituation.InvertedInterval, "inversion",
            "Inverted interval: older age above younger in a continuous segment",
            "change one radiometric date distribution",
            "track status becomes SEMANTIC_NO_SOLUTION; not silently smoothed away",
            System.Array.Empty<string>(),
            new[] { "inversion::all_candidates" },
            "{\"situation\":\"inverted_interval\",\"rule\":\"MONOTONE_ONLY_WITHIN_CONTINUOUS\"}"),

        new DossierScenario(DossierSituation.TiedAnchors, "tied-anchors",
            "Tied anchors: two event correlations score identically",
            "add one alternative event tie in one correlation group",
            "both equally-good candidates retained with stable order; no mean-merge",
            new[] { "tied-anchors::cand[A]", "tied-anchors::cand[B]" },
            System.Array.Empty<string>(),
            "{\"situation\":\"tied_anchors\",\"rule\":\"KEEP_ALL_EQUALLY_GOOD\"}"),

        new DossierScenario(DossierSituation.LongTailDistribution, "long-tail",
            "Long-tail age distribution: mixture with a reworked-age component",
            "replace one normal distribution by a mixture",
            "band widens from the tail; mean substitution explicitly disallowed",
            new[] { "long-tail::derivation" },
            System.Array.Empty<string>(),
            "{\"situation\":\"long_tail\",\"rule\":\"DISTRIBUTION_NOT_MEAN\"}"),

        new DossierScenario(DossierSituation.LocalRecompute, "local-recompute",
            "Local recompute: amend one source, only its dependent track recomputes",
            "supersede one radiometric date with a revised distribution",
            "unaffected tracks return byte-identical fingerprints; affected track gets new parent set",
            new[] { "local-recompute::track[0]" },
            System.Array.Empty<string>(),
            "{\"situation\":\"local_recompute\",\"rule\":\"DEPENDENCY_SCOPED_RECOMPUTE\"}"),

        new DossierScenario(DossierSituation.ConcurrentFreeze, "concurrent-freeze",
            "Concurrent freeze: two reviewers attempt to freeze different candidates",
            "two freeze decisions raced against the same derivation",
            "first freeze wins; second is recorded as REJECTED_OUTCOME and cannot mutate history",
            new[] { "concurrent-freeze::freeze[first]" },
            new[] { "concurrent-freeze::freeze[second]" },
            "{\"situation\":\"concurrent_freeze\",\"rule\":\"FREEZE_IS_LINEARIZABLE\"}"),
    };
}
