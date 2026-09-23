using System.Text.Json;

namespace AgeDepth;

/// <summary>Immutable source material. Identity is a stable id; content hash guards provenance.
/// Materials are never edited in place: a source change supersedes a row (valid_to closes).</summary>
public sealed record Material(string Id, string Kind, int Seq, string Hash, JsonElement Data, int ValidFrom, int ValidTo)
{
    public const int OpenEnded = int.MaxValue;
    public bool LiveAt(int seq) => ValidFrom <= seq && seq <= ValidTo;
}

public sealed record DepthDatumPayload(string Name, string Reference, double OffsetMeters, string Raw);
public sealed record BoundaryPayload(string BoundaryKind, Qty Top, Qty Bottom, string Reason);
public sealed record AnchorPayload(Qty Depth, Dist Age, double? ReservoirOffsetYears, string? EventLayerId, string Note);
public sealed record EventLayerPayload(string Name, Qty Depth, Dist? CorrelatedAge);
public sealed record ExclusionPayload(string AnchorId, bool Excluded, string Reason, string DecidedBy);

public static class MaterialKinds
{
    public const string DepthDatum = "depth-datum";
    public const string Boundary = "boundary";        // 分段缺口 / 不整合: explicit segment boundaries
    public const string Anchor = "anchor";            // 放射性年龄分布锚点
    public const string EventLayer = "event-layer";   // 事件层相关
    public const string Exclusion = "exclusion";      // 排除决定 (kept, never deleted)

    public static readonly string[] All = { DepthDatum, Boundary, Anchor, EventLayer, Exclusion };
}

public static class MaterialCodec
{
    public static T Read<T>(Material m) => m.Data.Deserialize<T>(JsonOpts.Default)!;
    public static JsonElement ToJson<T>(T payload) => JsonSerializer.SerializeToElement(payload, JsonOpts.Default);

    public static string HashOf(string kind, JsonElement data) =>
        Hashing.Sha256(kind + ":" + data.GetRawText());
}
