using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoCore;

/// <summary>
/// 反例片段用紧凑 DSL 描述分布（rebuild:normal:mean:sd 等）。
/// 正式导入则保留完整样本数组，二者互不替代。
/// </summary>
public sealed class FragmentSamplesConverter : JsonConverter<AgeSamples>
{
    public override AgeSamples Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ReplayRunner.ParseSamplesSpec(reader.GetString()!);
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var el = doc.RootElement;
            if (el.TryGetProperty("samplesCalABP", out var arr))
                return new AgeSamples
                {
                    DistributionType = el.TryGetProperty("distributionType", out var dt) ? dt.GetString()! : "empirical",
                    SamplesCalABP = arr.EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                    DegreesOfFreedom = el.TryGetProperty("degreesOfFreedom", out var df) ? df.GetInt32() : 8,
                    LongTail = el.TryGetProperty("longTail", out var lt) && lt.GetBoolean()
                };
        }
        throw new JsonException("无法识别的 samples 片段格式");
    }

    public override void Write(Utf8JsonWriter writer, AgeSamples value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new
        {
            distributionType = value.DistributionType,
            degreesOfFreedom = value.DegreesOfFreedom,
            longTail = value.LongTail,
            n = value.N
        }, options);
}
