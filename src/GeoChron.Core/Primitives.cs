using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GeoChron.Core;

/// <summary>
/// The three hard failure outcomes must stay distinguishable:
/// resource budget exhausted, corrupt format, semantically unsolvable.
/// Conflict covers optimistic-concurrency rejections.
/// </summary>
public enum FailureKind
{
    None = 0,
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnsolvable,
    Conflict
}

public sealed record Diagnostic(string Code, string Message, string Path, string Severity)
{
    public static Diagnostic Error(string code, string message, string path) => new(code, message, path, "error");
    public static Diagnostic Info(string code, string message, string path) => new(code, message, path, "info");
}

public sealed class OpResult
{
    public FailureKind Failure { get; private init; }
    public bool Ok => Failure == FailureKind.None;
    public List<Diagnostic> Diagnostics { get; } = new();
    public string? EventId { get; private init; }

    public static OpResult Success(string? eventId = null, IEnumerable<Diagnostic>? diags = null)
    {
        var r = new OpResult { Failure = FailureKind.None, EventId = eventId };
        if (diags != null) r.Diagnostics.AddRange(diags);
        return r;
    }

    public static OpResult Fail(FailureKind kind, IEnumerable<Diagnostic> diags)
    {
        var r = new OpResult { Failure = kind };
        r.Diagnostics.AddRange(diags);
        return r;
    }

    public static OpResult Fail(FailureKind kind, params Diagnostic[] diags) => Fail(kind, (IEnumerable<Diagnostic>)diags);
}

public static class Hashing
{
    public static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string ShortId(string prefix, string seed, int len = 12) =>
        $"{prefix}-{Sha256(seed)[..len]}";

    /// <summary>Canonical JSON: object keys sorted, no whitespace. Stable across runs and processes.</summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteCanonical(node, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    WriteCanonical(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteCanonical(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                sb.Append(val.ToJsonString());
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
