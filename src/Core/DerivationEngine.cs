using System.Text.Json;
using ChronoCore.Domain;

namespace ChronoCore.Core;

public sealed record DerivationRecord(
    string DerivationId,
    string Kind,
    string InputDigest,
    string ParamsVersion,
    IReadOnlyList<string> Parents,
    string Status,            // ok | format-corrupt | resource-exhausted | semantic-unsolvable
    string? Reason,
    string PayloadJson);

public sealed record CandidateSetResult(
    IReadOnlyList<CandidateModel> Models,
    IReadOnlyList<string> EqualBestIds,
    IReadOnlyList<string> RejectedIds);

/// <summary>
/// Content-addressed derivation layer. Every result records an input digest, the rule version and
/// its parent derivations/materials. Identical inputs produce the same stable id, so local source
/// changes recompute only the affected subgraph.
/// </summary>
public sealed class DerivationEngine
{
    public const string StatusOk = "ok";
    public const string StatusFormatCorrupt = "format-corrupt";
    public const string StatusResourceExhausted = "resource-exhausted";
    public const string StatusSemanticUnsolvable = "semantic-unsolvable";

    public DerivationRecord NormalizeProfile(NormalizeInput input, RuleSet rules)
    {
        var digest = StableId.Digest(new
        {
            input.CoreId,
            input.Datum.Name,
            segments = input.Segments.Select(s => new { s.Id, s.Top, s.Bottom, s.Facies }),
            anchors = input.Anchors.Select(a => new
            {
                a.Id, a.Depth, a.Method,
                age = new { a.Age.Kind, a.Age.MedianYrBp, a.Age.SigmaYr, a.Age.SampleCount }
            }),
            offsets = input.ReservoirOffsets.Select(o => new { o.Id, o.AnchorId, o.DeltaAge }),
            events = input.EventBeds.Select(e => new { e.Id, e.Depth, e.Name, e.CorrelatesTo }),
            boundaries = input.Boundaries.Select(b => new { b.Id, b.Depth, b.Kind }),
            exclusions = input.Exclusions.Select(x => new { x.Id, x.TargetId, x.RevokesDecisionId })
        });
        var id = "der:norm:" + StableId.Of(rules.Version, digest);
        var profile = Normalizer.Normalize(input, rules);
        var fatal = profile.Diagnostics.Where(d => d.Severity == "error").ToList();
        var status = fatal.Count == 0 ? StatusOk : StatusSemanticUnsolvable;
        var payload = JsonSerializer.Serialize(new { profile, fatal }, JsonOptions.Compact);
        return new DerivationRecord(id, "normalize-profile", digest, rules.Version,
            new[] { digest }, status, fatal.Count == 0 ? null : string.Join("; ", fatal.Select(f => f.Message)), payload);
    }

    public (DerivationRecord record, CandidateSetResult result) BuildCandidates(DerivationRecord normRecord, RuleSet rules)
    {
        var profile = JsonSerializer.Deserialize<NormalizedProfile>(
            JsonDocument.Parse(normRecord.PayloadJson).RootElement.GetProperty("profile").GetRawText(),
            JsonOptions.Compact)!;
        var models = CandidateEngine.Build(profile, rules);
        var best = models.Where(m => m.Status == CandidateEngine.Ok).Select(m => m.Score).DefaultIfEmpty(double.PositiveInfinity).Min();
        var equalBest = models.Where(m => m.Status == CandidateEngine.Ok && Math.Abs(m.Score - best) <= 1e-6 * Math.Max(1.0, best))
            .Select(m => m.ModelId).ToList();
        var rejected = models.Where(m => m.Status != CandidateEngine.Ok).Select(m => m.ModelId).ToList();
        var result = new CandidateSetResult(models, equalBest, rejected);

        var id = "der:cands:" + StableId.Of(rules.Version, normRecord.DerivationId);
        var status = models.Any(m => m.Status == CandidateEngine.Ok)
            ? StatusOk
            : StatusSemanticUnsolvable;
        var payload = JsonSerializer.Serialize(result, JsonOptions.Compact);
        return (new DerivationRecord(id, "candidate-models", normRecord.DerivationId, rules.Version,
            new[] { normRecord.DerivationId }, status,
            rejected.Count > 0 ? $"{rejected.Count} candidate(s) rejected; rejected candidates retained" : null,
            payload), result);
    }

    public (DerivationRecord record, SamplingOutcome outcome) SampleAnchorBand(string anchorId, AgeDistribution dist, RuleSet rules)
    {
        var digest = StableId.Digest(new { anchorId, dist.Kind, dist.MedianYrBp, dist.SampleCount, dist.SigmaYr });
        var id = "der:band:" + StableId.Of(rules.Version, digest);
        var outcome = CandidateEngine.SampleBand(dist, rules);
        var status = outcome.Status switch
        {
            "ok" => StatusOk,
            "resource-exhausted" => StatusResourceExhausted,
            _ => StatusSemanticUnsolvable
        };
        var payload = JsonSerializer.Serialize(new { anchorId, outcome }, JsonOptions.Compact);
        return (new DerivationRecord(id, "sample-band", digest, rules.Version, new[] { anchorId, digest },
            status, outcome.Reason, payload), outcome);
    }
}
