using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DepthAge.Core;

/// <summary>不可变材料信封：身份 = 内容哈希，原始表示原样保存，规范化只发生在派生层。</summary>
public sealed record MaterialEnvelope(string Id, string Kind, string Hash, string RawJson, long Seq, string Created)
{
    public const string DepthDatumKind = "depth-datum";
    public const string SegmentKind = "core-segment";
    public const string AnchorKind = "age-anchor";
    public const string EventLayerKind = "event-layer";
    public const string ExclusionKind = "exclusion";
}

public sealed record DepthDatumDef(string Name, string? Note);

/// <summary>岩芯分段：上下界带单位与坐标基准；TopBoundary/BottomBoundary 是显式边界（continuous/gap/unconformity/...）。</summary>
public sealed record CoreSegmentDef(
    string Segment,
    Quantity Top,
    Quantity Bottom,
    string TopBoundary,
    string BottomBoundary);

/// <summary>
/// 年龄锚点：深度、年龄分布、储层偏移（null 表示沿用参数默认值，显式 0 表示无偏移）。
/// Supersedes 用于“只改动一个直接来源”的源数据修订：旧材料保留，新材料指向旧锚点引用。
/// </summary>
public sealed record AgeAnchorDef(
    string Anchor,
    Quantity Depth,
    AgeDistribution Age,
    Quantity? ReservoirOffset,
    string? Material,
    string? Method,
    string? Supersedes);

/// <summary>事件层（如火山灰/洪水层）及其与已知事件的相关年龄分布。</summary>
public sealed record EventLayerDef(
    string Layer,
    Quantity Depth,
    string CorrelatesTo,
    AgeDistribution? EventAge);

/// <summary>排除决定本身也是不可变材料：目标、理由、决定人全部保留。</summary>
public sealed record ExclusionDef(string Target, string Reason, string DecidedBy);

public static class MaterialHashes
{
    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static MaterialEnvelope Create(string kind, string rawJson, long seq, DateTimeOffset? created = null)
    {
        var hash = Sha256Hex(rawJson);
        return new MaterialEnvelope($"{kind}:{hash[..12]}", kind, hash, rawJson, seq,
            (created ?? DateTimeOffset.UtcNow).ToString("O"));
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Pretty : Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Pretty);
}
