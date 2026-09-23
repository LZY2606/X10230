using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>物理量：始终携带单位与坐标/时间基准，并保留导入时的原始表示。</summary>
public sealed record Quantity(double? Value, string Unit, string Datum, string Raw)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["value"] = Value,
        ["unit"] = Unit,
        ["datum"] = Datum,
        ["raw"] = Raw,
    };
}

public static class MaterialKind
{
    public const string DepthBenchmark = "depth-benchmark";
    public const string SegmentGap = "segment-gap";
    public const string Unconformity = "unconformity";
    public const string RadiometricAge = "radiometric-age";
    public const string ReservoirOffset = "reservoir-offset";
    public const string EventLayer = "event-layer";
    public const string Exclusion = "exclusion-decision";

    public static readonly string[] All =
    [
        DepthBenchmark, SegmentGap, Unconformity, RadiometricAge,
        ReservoirOffset, EventLayer, Exclusion,
    ];
}

/// <summary>不可变材料：导入后内容不修改，只能被新材料取代（supersededBy）。</summary>
public sealed record Material(
    string Id,
    int Ord,
    string Kind,
    JsonNode Payload,
    string ContentHash,
    string? SupersededBy = null)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["id"] = Id,
        ["ord"] = Ord,
        ["kind"] = Kind,
        ["payload"] = Payload.DeepClone(),
        ["contentHash"] = ContentHash,
        ["supersededBy"] = SupersededBy,
    };
}

public static class DecisionKind
{
    public const string ParameterRevision = "parameter-revision";
    public const string AcceptCandidate = "accept-candidate";
    public const string RejectCandidate = "Reject-candidate";
    public const string Freeze = "freeze";
    public const string Conflict = "conflict";
}

/// <summary>可修改决定层：每条决定版本化保存，从不原地改写。</summary>
public sealed record Decision(
    string Id,
    int Ord,
    string Kind,
    JsonNode Payload,
    string Status)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["id"] = Id,
        ["ord"] = Ord,
        ["kind"] = Kind,
        ["payload"] = Payload.DeepClone(),
        ["status"] = Status,
    };
}

/// <summary>机器可读取事件：记录输入摘要、参数版本、父级依赖与受影响对象。</summary>
public sealed record EventRecord(
    long Seq,
    string Id,
    string Type,
    JsonNode Payload,
    string InputDigest,
    int ParamVersion,
    string[] Parents,
    string[] Affected,
    string CreatedAt)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["seq"] = Seq,
        ["id"] = Id,
        ["type"] = Type,
        ["payload"] = Payload.DeepClone(),
        ["inputDigest"] = InputDigest,
        ["paramVersion"] = ParamVersion,
        ["parents"] = new JsonArray(Parents.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        ["affected"] = new JsonArray(Affected.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        ["createdAt"] = CreatedAt,
    };
}

public sealed record Diagnostic(string Code, string Message, string Path, string? MaterialId)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["code"] = Code,
        ["message"] = Message,
        ["path"] = Path,
        ["materialId"] = MaterialId,
    };
}

/// <summary>连续（不断）层段：深度缺口与不整合是显式边界。</summary>
public sealed record Segment(
    string Id,
    double TopM,
    double BottomM,
    string TopBoundary,
    string BottomBoundary)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["id"] = Id,
        ["topM"] = TopM,
        ["bottomM"] = BottomM,
        ["topBoundary"] = TopBoundary,
        ["bottomBoundary"] = BottomBoundary,
        ["unit"] = "m",
        ["datum"] = "mKB",
    };
}

public sealed record RateInterval(
    double FromDepthM, double ToDepthM,
    double P05, double P50, double P95)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["fromDepthM"] = FromDepthM,
        ["toDepthM"] = ToDepthM,
        ["p05"] = P05, ["p50"] = P50, ["p95"] = P95,
        ["unit"] = "cm/ka",
    };
}

public sealed record BandNode(double DepthM, double P05, double P50, double P95)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["depthM"] = DepthM, ["p05"] = P05, ["p50"] = P50, ["p95"] = P95,
        ["ageUnit"] = "ka BP",
    };
}

public static class CandidateStatus
{
    public const string Active = "active";
    public const string Frozen = "frozen";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

/// <summary>一套可辩护候选模型。等优候选全部保留。</summary>
public sealed record Candidate(
    string Id,
    string SegmentId,
    string[] AnchorIds,
    RateInterval[] Rates,
    BandNode[] Band,
    double Score,
    bool EqualRank,
    string Status,
    int Version,
    string Digest,
    string? InvalidatedReason = null)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["id"] = Id,
        ["segmentId"] = SegmentId,
        ["anchorIds"] = new JsonArray(AnchorIds.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["rates"] = new JsonArray(Rates.Select(r => r.ToJson()).ToArray()),
        ["band"] = new JsonArray(Band.Select(b => b.ToJson()).ToArray()),
        ["score"] = Score,
        ["equalRank"] = EqualRank,
        ["status"] = Status,
        ["version"] = Version,
        ["digest"] = Digest,
        ["invalidatedReason"] = InvalidatedReason,
    };
}

public static class StableId
{
    public static string For(string prefix, string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"{prefix}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public static string Digest(JsonNode node)
    {
        var canonical = Canonical(node);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>键排序后的规范 JSON，保证等价输入得到稳定摘要与稳定顺序。</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",",
            obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
               .Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(Canonical)) + "]",
        JsonValue val => val.ToJsonString(),
        _ => node.ToJsonString(),
    };
}
