using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DepthAge.Domain;

public enum ResultStatus
{
    Ok,
    BudgetExhausted,
    FormatCorrupt,
    SemanticUnsolvable
}

public sealed record Diagnostic(string Code, string Message, string? MaterialId, string? Field, string? Position);

public sealed record DepthAmount(double Value, string Unit, string Datum, string Raw);

public sealed record AgeDistribution(string Unit, double[] Samples, string Raw);

public sealed record CoreSegmentBody(DepthAmount Top, DepthAmount Base, string Label);

public sealed record SegmentBoundaryBody(DepthAmount From, DepthAmount To, string BoundaryKind, string Note);

public sealed record AgeAnchorBody(DepthAmount Depth, AgeDistribution Distribution, string? EventLayerId, string Lab, string Label);

public sealed record ReservoirOffsetBody(string AnchorId, double Value, string Unit, string Raw);

public sealed record EventLayerBody(DepthAmount Depth, string Name, string? CorrelatesTo);

public sealed record ExclusionBody(string TargetId, string Reason, bool Active);

public static class MaterialKinds
{
    public const string CoreSegment = "core-segment";
    public const string SegmentBoundary = "segment-boundary";
    public const string AgeAnchor = "age-anchor";
    public const string ReservoirOffset = "reservoir-offset";
    public const string EventLayer = "event-layer";
    public const string Exclusion = "exclusion";
}

public sealed record MaterialEnvelope(string Id, string Kind, string Hash, string Body, string Raw)
{
    public static MaterialEnvelope Create<T>(string id, string kind, T body, string raw)
    {
        var json = JsonSerializer.Serialize(body, JsonUtil.Options);
        return new MaterialEnvelope(id, kind, JsonUtil.Hash(kind + "|" + json), json, raw);
    }

    public T? ReadBody<T>()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Body, JsonUtil.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string ShortHash(string text) => Hash(text)[..12];
}

public sealed record ModelParameters(int Version, int MaxCandidates, double RateFloor, bool ReservoirCorrection, string Interpolation)
{
    public static ModelParameters Default => new(1, 64, 0.0, false, "piecewise-linear");
}

public sealed record CandidateModel(string Id, string RunKey, double[] DepthsM, double[] AgesKa, double Score, bool EquallyGood, string Provenance);

public sealed record RateInterval(string RunKey, string FromAnchor, string ToAnchor, double DepthFromM, double DepthToM, double MinRate, double MaxRate, string Unit);

public sealed record BandPoint(double DepthM, double MinAgeKa, double MaxAgeKa);

public sealed record RunResult(
    string Key,
    string SegmentId,
    double TopM,
    double BaseM,
    string TopBoundary,
    string BaseBoundary,
    string[] AnchorIds,
    CandidateModel[] Candidates,
    RateInterval[] Rates,
    BandPoint[] Band,
    Diagnostic[] Diagnostics,
    string InputDigest);

public sealed record ComputationResult(
    ResultStatus Status,
    Diagnostic[] Diagnostics,
    RunResult[] Runs,
    string InputDigest,
    int ParameterVersion,
    string ResultId);
