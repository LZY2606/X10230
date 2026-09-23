using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreChronology;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(DepthDatum), "DepthDatum")]
[JsonDerivedType(typeof(SegmentGap), "SegmentGap")]
[JsonDerivedType(typeof(AgeAnchor), "AgeAnchor")]
[JsonDerivedType(typeof(ReservoirOffset), "ReservoirOffset")]
[JsonDerivedType(typeof(EventLayer), "EventLayer")]
internal abstract class MaterialJsonMarker;

public static class MaterialJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static Material Read(string kind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var obj = doc.RootElement;
        var sourceRef = obj.GetProperty("sourceRef").GetString() ?? "";
        var sourceLine = obj.TryGetProperty("sourceLine", out var sl) ? sl.GetInt32() : 0;

        Material m = kind switch
        {
            nameof(DepthDatum) => new DepthDatum(
                obj.GetProperty("id").GetString()!,
                obj.GetProperty("depth").Deserialize<Measure>(Options)!,
                obj.GetProperty("coordinateBasis").GetString() ?? "",
                obj.GetProperty("orientationConvention").GetString() ?? ""),
            nameof(SegmentGap) => new SegmentGap(
                obj.GetProperty("id").GetString()!,
                obj.GetProperty("depthTop"u8.ToString(), Options),
                obj.GetProperty("depthBottom").Deserialize<Measure>(Options)!,
                Enum.Parse<GapKind>(obj.GetProperty("kind").GetString()!),
                obj.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""),
            nameof(AgeAnchor) => new AgeAnchor(
                obj.GetProperty("id").GetString()!,
                obj.GetProperty("depth").Deserialize<Measure>(Options)!,
                ReadDistribution(obj.GetProperty("distribution")),
                Enum.Parse<AnchorKind>(obj.GetProperty("anchorKind").GetString()!),
                obj.TryGetProperty("reservoirOffsetId", out var ro) && ro.ValueKind == JsonValueType.String
                    ? ro.GetString() : null,
                obj.TryGetProperty("eventLayerId", out var ev) && ev.ValueKind == JsonValueType.String
                    ? ev.GetString() : null,
                obj.TryGetProperty("optional", out var op) && op.GetBoolean()),
            nameof(ReservoirOffset) => new ReservoirOffset(
                obj.GetProperty("id").GetString()!,
                obj.GetProperty("depth").Deserialize<Measure>(Options)!,
                obj.GetProperty("offset").Deserialize<Measure>(Options)!,
                obj.TryGetProperty("applied", out var ap) && ap.GetBoolean()),
            nameof(EventLayer) => new EventLayer(
                obj.GetProperty("id").GetString()!,
                obj.GetProperty("depth").Deserialize<Measure>(Options)!,
                obj.GetProperty("label").GetString() ?? "",
                obj.TryGetProperty("globalCorrelation", out var gc) && gc.GetBoolean()),
            _ => throw new JsonException($"unknown material kind: {kind}"),
        };
        return m with { SourceRef = sourceRef, SourceLine = sourceLine };
    }

    private static AgeDistribution ReadDistribution(JsonElement el)
    {
        string kind = el.GetProperty("$dist").GetString()!;
        return kind switch
        {
            "gaussian" => new GaussianDistribution(
                el.GetProperty("meanCalYrBp").GetDouble(),
                el.GetProperty("sigmaYr").GetDouble()),
            "uniform" => new UniformDistribution(
                el.GetProperty("lowCalYrBp").GetDouble(),
                el.GetProperty("highCalYrBp").GetDouble()),
            "empirical" => new EmpiricalDistribution(
                el.EnumerateArray("points").Select(p =>
                    (p.GetProperty("ageCalYrBp").GetDouble(),
                     p.GetProperty("weight").GetDouble())).ToArray()),
            _ => throw new JsonException($"unknown distribution: {kind}"),
        };
    }

    public static string Write(Material m) => JsonSerializer.Serialize(m, m.GetType(), Options);
    public static string KindOf(Material m) => m.GetType().Name;
}

internal static class JsonElementExtensions
{
    public static IEnumerable<JsonProperty> EnumerateArray(this JsonElement _, string __) =>
        throw new NotSupportedException();
}
