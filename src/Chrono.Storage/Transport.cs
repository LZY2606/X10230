using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Domain;

namespace Chrono.Storage;

/// <summary>Project export = envelope of immutable events plus schema marker.
/// Re-import preserves event ids, seq order, branch and provenance links.</summary>
public sealed record ProjectPackage(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("exportedAt")] string ExportedAt,
    [property: JsonPropertyName("events")] IReadOnlyList<StoredEvent> Events)
{
    public const string SchemaVersion = "chrono-gsb/1.0";
}

public static class Transport
{
    public static string Serialize(IEnumerable<StoredEvent> events, string projectId) =>
        JsonSerializer.Serialize(new ProjectPackage(
            ProjectPackage.SchemaVersion, projectId, DateTime.UtcNow.ToString("O"), events.ToList()),
            new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true });

    /// <summary>Returns FormatCorrupt for malformed JSON / schema mismatch,
    /// distinct from semantic and budget failures.</summary>
    public static Outcome<ProjectPackage> TryDeserialize(string json)
    {
        try
        {
            var pkg = JsonSerializer.Deserialize<ProjectPackage>(json);
            if (pkg is null)
                return Outcome<ProjectPackage>.Format(new[] { new Diagnostic("PKG-NULL", "导出文件反序列化为空", "package") });
            if (pkg.Schema != ProjectPackage.SchemaVersion)
                return Outcome<ProjectPackage>.Format(new[] { new Diagnostic("PKG-SCHEMA",
                    $"导出 schema 不匹配：{pkg.Schema} != {ProjectPackage.SchemaVersion}", "package.schema") });
            if (pkg.Events is null || pkg.Events.Count == 0)
                return Outcome<ProjectPackage>.Format(new[] { new Diagnostic("PKG-EMPTY", "导出文件不含任何事件", "package.events") });
            return Outcome<ProjectPackage>.Ok(pkg);
        }
        catch (JsonException ex)
        {
            return Outcome<ProjectPackage>.Format(new[] { new Diagnostic("PKG-JSON", $"格式损坏：{ex.Message}", "package") });
        }
    }
}
