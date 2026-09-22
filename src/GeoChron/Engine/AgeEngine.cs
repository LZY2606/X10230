using System.Text.Json.Nodes;
using GeoChron.Derivation;
using GeoChron.Domain;

namespace GeoChron.Engine;

/// <summary>
/// Server-side depth-age engine.
/// - ages are monotonic with depth only inside uninterrupted sections;
///   hiatuses, unconformities and faults are explicit boundaries.
/// - anchor distributions are resampled (never replaced by the mean).
/// - every co-optimal candidate is retained with a stable ordering.
/// </summary>
public sealed partial class AgeEngine
{
    readonly RandomSeeded _rng = new();

    public RunResult Run(
        NormalizedSet input,
        EngineParams p,
        IReadOnlyDictionary<string, string>? materialVersions = null,
        IReadOnlyList<string>? parentComputeIds = null)
    {
        var diags = new List<Diag>(input.Diags);

        if (diags.Any(d => d.Severity == "error" && CorruptCodes.Contains(d.Code)))
            return Fail(RunStatus.CorruptInput, input, p, diags, materialVersions, parentComputeIds);
        if (diags.Any(d => d.Severity == "error"))
            return Fail(RunStatus.SemanticUnfeasible, input, p, diags, materialVersions, parentComputeIds);

        var sections = BuildSections(input, diags);
        if (diags.Any(d => d.Severity == "error"))
            return Fail(RunStatus.SemanticUnfeasible, input, p, diags, materialVersions, parentComputeIds, sections);

        var eligible = EligibleAnchors(input, p, diags, out var tieGroups);
        if (eligible.Count == 0)
        {
            diags.Add(Diag.Error("NO_ELIGIBLE_ANCHORS", "engine", "no anchor matches the model time basis; cannot build any age-depth model"));
            return Fail(RunStatus.SemanticUnfeasible, input, p, diags, materialVersions, parentComputeIds, sections);
        }

        var combinations = EnumerateCombinations(eligible, tieGroups, p.MaxCandidates, diags, out var truncated, out var enumerated);
        if (truncated)
            return new RunResult
            {
                Status = RunStatus.BudgetExhausted,
                Params = p,
                InputSummary = Summary(input, p),
                Diags = diags,
                EligibleAnchorLineages = eligible.ConvertAll(a => a.Lineage),
                Sections = sections,
                Candidates = Array.Empty<CandidateResult>(),
                EnumerationTruncated = true,
                Enumerated = enumerated,
                MaterialVersions = materialVersions ?? new Dictionary<string, string>(),
                ParentComputeIds = parentComputeIds ?? Array.Empty<string>()
            };

        var evaluated = combinations
            .Select(comb => Evaluate(comb, sections, input, p))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        return new RunResult
        {
            Status = RunStatus.Ok,
            Params = p,
            InputSummary = Summary(input, p),
            Diags = diags,
            EligibleAnchorLineages = eligible.ConvertAll(a => a.Lineage),
            Sections = sections,
            Candidates = evaluated,
            EnumerationTruncated = false,
            Enumerated = enumerated,
            MaterialVersions = materialVersions ?? new Dictionary<string, string>(),
            ParentComputeIds = parentComputeIds ?? Array.Empty<string>()
        };
    }

    static readonly HashSet<string> CorruptCodes = new() { "FRAGMENT_CORRUPT", "DEPTH_UNPARSEABLE" };

    static RunResult Fail(RunStatus status, NormalizedSet input, EngineParams p, List<Diag> diags,
        IReadOnlyDictionary<string, string>? mv, IReadOnlyList<string>? parents, List<Section>? sections = null) => new()
    {
        Status = status,
        Params = p,
        InputSummary = Summary(input, p),
        Diags = diags,
        EligibleAnchorLineages = Array.Empty<string>(),
        Sections = sections ?? Array.Empty<Section>(),
        Candidates = Array.Empty<CandidateResult>(),
        MaterialVersions = mv ?? new Dictionary<string, string>(),
        ParentComputeIds = parents ?? Array.Empty<string>()
    };

    public static string Summary(NormalizedSet input, EngineParams p)
    {
        var o = new JsonObject
        {
            ["datum"] = input.Datum,
            ["paramsHash"] = p.Hash,
            ["segments"] = new JsonArray(input.Segments
                .Select(s => (JsonNode)new JsonObject
                {
                    ["lineage"] = s.Lineage,
                    ["topM"] = s.Top.Convertible ? s.Top.Metres : null,
                    ["bottomM"] = s.Bottom.Convertible ? s.Bottom.Metres : null,
                    ["kind"] = s.Kind.ToString()
                }).ToArray()),
            ["gaps"] = new JsonArray(input.Gaps
                .Select(g => (JsonNode)new JsonObject
                {
                    ["lineage"] = g.Lineage,
                    ["kind"] = g.Kind.ToString()
                }).ToArray()),
            ["anchors"] = new JsonArray(input.Anchors
                .Select(a => (JsonNode)new JsonObject
                {
                    ["lineage"] = a.Lineage,
                    ["depthM"] = a.DepthRaw.Convertible ? a.DepthM : null,
                    ["mean14CBP"] = a.Mean14CBP,
                    ["sigmaYears"] = a.Sigma14Yr,
                    ["basis"] = a.Basis,
                    ["distribution"] = a.Distribution.ToString(),
                    ["tailDraws"] = a.TailSize,
                    ["excluded"] = a.Excluded
                }).ToArray()),
            ["events"] = new JsonArray(input.Events
                .Select(e => (JsonNode)new JsonObject
                {
                    ["lineage"] = e.Lineage,
                    ["usedAsAnchor"] = e.UsedAsAnchor,
                    ["excluded"] = e.Excluded
                }).ToArray())
        };
        return CanonicalJson.ToText(o);
    }
}
