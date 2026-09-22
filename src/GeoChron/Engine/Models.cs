using System.Text.Json.Nodes;
using GeoChron.Derivation;

namespace GeoChron.Engine;

public sealed record EngineParams(
    int ParamVersion,
    int Draws,
    int GridPerSection,
    int MaxCandidates,
    IReadOnlyList<string> AcceptedBasis,
    string PrimaryBasis)
{
    public JsonObject ToJson() => new()
    {
        ["paramVersion"] = ParamVersion,
        ["draws"] = Draws,
        ["gridPerSection"] = GridPerSection,
        ["maxCandidates"] = MaxCandidates,
        ["acceptedBasis"] = new JsonArray(AcceptedBasis.Select(b => (JsonNode)b).ToArray()),
        ["primaryBasis"] = PrimaryBasis
    };

    public static EngineParams FromJson(JsonObject o) => new(
        o["paramVersion"]!.GetValue<int>(),
        o["draws"]!.GetValue<int>(),
        o["gridPerSection"]!.GetValue<int>(),
        o["maxCandidates"]!.GetValue<int>(),
        o["acceptedBasis"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
        o["primaryBasis"]!.GetValue<string>());

    public string Hash => Domain.CanonicalJson.HashHex(ToJson(), "params_");
}

public enum RunStatus { Ok, SemanticUnfeasible, BudgetExhausted, CorruptInput }

public sealed record RateInterval(string SectionLineage, double DepthM, double P05, double P50, double P95);
public sealed record BandPoint(double DepthM, double P05, double P50, double P95);
public sealed record Band(string SectionLineage, IReadOnlyList<BandPoint> Points);
public sealed record InversionEvidence(string SectionLineage, string SegmentLineage, double DrawsFlipped, string Detail);

public sealed class CandidateResult
{
    public required string CandidateId { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<string> AnchorLineages { get; init; }
    public required IReadOnlyList<string> TieResolutions { get; init; }
    public double Score { get; init; }
    public double ExpectedLogLikelihood { get; init; }
    public required IReadOnlyList<Band> Bands { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required IReadOnlyList<InversionEvidence> Inversions { get; init; }
    public JsonObject ToJson() => new()
    {
        ["candidateId"] = CandidateId,
        ["key"] = Key,
        ["anchorLineages"] = new JsonArray(AnchorLineages.Select(x => (JsonNode)x).ToArray()),
        ["tieResolutions"] = new JsonArray(TieResolutions.Select(x => (JsonNode)x).ToArray()),
        ["score"] = Score,
        ["expectedLogLikelihood"] = ExpectedLogLikelihood,
        ["inversions"] = new JsonArray(Inversions.Select(x => (JsonNode)new JsonObject
        {
            ["section"] = x.SectionLineage,
            ["segment"] = x.SegmentLineage,
            ["drawsFlipped"] = x.DrawsFlipped,
            ["detail"] = x.Detail
        }).ToArray())
    };
}

public sealed record Section(
    string Lineage, string Name, int Direction,
    IReadOnlyList<CoreSeg> Segments,
    IReadOnlyList<string> BoundaryGapLineages,
    double TopM, double BottomM);

public sealed class RunResult
{
    public required RunStatus Status { get; init; }
    public required EngineParams Params { get; init; }
    public required string InputSummary { get; init; }
    public required IReadOnlyList<Domain.Diag> Diags { get; init; }
    public required IReadOnlyList<string> EligibleAnchorLineages { get; init; }
    public required IReadOnlyList<Section> Sections { get; init; }
    public required IReadOnlyList<CandidateResult> Candidates { get; init; }
    public bool EnumerationTruncated { get; init; }
    public int Enumerated { get; init; }
    public required IReadOnlyDictionary<string, string> MaterialVersions { get; init; }
    public required IReadOnlyList<string> ParentComputeIds { get; init; }
}
