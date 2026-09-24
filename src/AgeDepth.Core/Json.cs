using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Stringify<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
