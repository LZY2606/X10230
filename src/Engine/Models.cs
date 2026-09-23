using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DepthAgeReview.Domain;

namespace DepthAgeReview.Engine;

public enum InterpolationMethod { Linear, Pchip }

/// <summary>一个有效深度上的年龄带（cal a BP 分位数）。</summary>
public sealed record AgeBandNode(string SegmentId, double DepthCm, double Q025, double Q50, double Q975);

/// <summary>一段内相邻锚点间（或锚点到段端）的沉积速率区间 cm/ka。</summary>
public sealed record RateInterval(string SegmentId, double FromCm, double ToCm, double RateLo, double RateHi, double RateMedian);

/// <summary>候选深度-年龄模型。多个等优模型必须全部保留（<see cref="EquallyOptimal"/>）。</summary>
public sealed record CandidateModel(
    string ModelId,
    string Fingerprint,
    int Rank,
    bool EquallyOptimal,
    double Score,
    InterpolationMethod Method,
    IReadOnlyList<string> ChosenAnchorIds,
    IReadOnlyList<AgeBandNode> Bands,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<ModelFlag> Flags,
    IReadOnlySet<string> AffectedSegmentIds,
    ModelProvenance Provenance)
{
    public string Status { get; init; } = "candidate"; // candidate | accepted | rejected | frozen
    public string? DecisionId { get; init; }
}

public enum FlagCode
{
    LongTail, InvertedDraws, SoftInversion, DuplicateDepth, TieAtDepth,
    GapBoundary, UnconformityBoundary, EventCorrelated, BudgetTruncated
}

public sealed record ModelFlag(FlagCode Code, string Message, string? ObjectId = null);

/// <summary>派生结果的血缘：输入摘要、参数版本、父级依赖。</summary>
public sealed record ModelProvenance(
    string MaterialVersion,
    string ParamVersion,
    string ParentModelId,
    string InputDigest,
    IReadOnlyList<string> DependencyIds,
    string DerivedBy,
    DateTimeOffset DerivedAt);

public sealed record BuildOptions(
    int MonteCarloDraws = 400,
    int CandidateBudget = 20000,
    double TieEpsilon = 1e-9,
    double LongTailRightRatio = 1.35,
    double InversionHardProbability = 0.95)
{
    public static readonly BuildOptions Default = new();
}

/// <summary>一次建模的完整结果。三种坏结果与 Ok 严格区分。</summary>
public sealed record BuildResult(
    BuildOutcomeKind Outcome,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<TieGroup> TieGroups,
    BuildOptions Options,
    int EnumeratedBeforeBudget,
    bool BudgetReached)
{
    public IReadOnlyList<CandidateModel> AcceptedBest => Candidates.Where(c => c.EquallyOptimal).ToList();
}

/// <summary>并列锚点（同一有效深度上的多个可选锚点），深度重复场景的结构化状态。</summary>
public sealed record TieGroup(string SegmentId, double DepthCm, IReadOnlyList<string> AnchorIds, string Reason);

internal static class Fingerprints
{
    public static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string Digest(object o)
    {
        using var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, o, new JsonSerializerOptions { WriteIndented = false });
        return Sha256(Encoding.UTF8.GetString(ms.ToArray()));
    }
}
