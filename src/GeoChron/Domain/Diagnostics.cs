using System.Text.Json.Nodes;

namespace GeoChron.Domain;

/// <summary>
/// A diagnostic always carries a location (import fragment / material / section),
/// a machine code and severity. Incomplete inputs never silently become nulls.
/// </summary>
public sealed record Diag(string Code, string Severity, string Location, string Message)
{
    public JsonObject ToJson() => new()
    {
        ["code"] = Code,
        ["severity"] = Severity,
        ["location"] = Location,
        ["message"] = Message
    };

    public static Diag Error(string code, string location, string message) => new(code, "error", location, message);
    public static Diag Warn(string code, string location, string message) => new(code, "warning", location, message);
}
