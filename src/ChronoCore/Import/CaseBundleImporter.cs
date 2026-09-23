using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoCore.Materials;
using ChronoCore.Quantities;

namespace ChronoCore.Import;

/// <summary>从离线案例包（文件名→内容）导入不可变材料。</summary>
public sealed class CaseBundleImporter
{
    private sealed class ItemLimit
    {
        public required string File; public required int Max;
        public required string Code; public required string Message;
    }

    private static readonly ItemLimit[] Limits =
    {
        new() { File = "sections.json", Max = 64, Code = "budget/sections", Message = "分段数超出资源预算" },
        new() { File = "boundaries.json", Max = 64, Code = "budget/boundaries", Message = "边界数超出资源预算" },
        new() { File = "anchors.json", Max = 512, Code = "budget/anchors", Message = "锚点数超出资源预算" },
        new() { File = "event_layers.json", Max = 128, Code = "budget/events", Message = "事件层数超出资源预算" },
        new() { File = "exclusions.json", Max = 512, Code = "budget/exclusions", Message = "排除记录数超出资源预算" },
    };

    private static readonly string[] Required =
    {
        "datum.json", "sections.json", "boundaries.json",
        "anchors.json", "reservoir_offsets.json", "event_layers.json", "exclusions.json",
    };

    private readonly List<ImportDiagnostic> _diag = new();

    public ImportResult Import(IReadOnlyDictionary<string, string> files)
    {
        var digest = ComputeDigest(files);

        foreach (var req in Required)
            if (!files.TryGetValue(req, out var text) || string.IsNullOrWhiteSpace(text))
                Error(new SourcePosition(req, null, null), "format/missing-file", $"缺少必需文件 {req}");

        if (_diag.Count > 0)
            return ImportResult.Fail(ImportStatus.MalformedFormat, _diag, digest);

        JsonDocument datumDoc = ParseDoc("datum.json", files["datum.json"])!;
        var datumRoot = datumDoc.RootElement;
        string datumId = Str(datumRoot, "id", "datum.json");
        var datum = new DepthDatum(
            datumId,
            ParseEnum<DepthDatumKind>(datumRoot, "kind", "datum.json"),
            Str(datumRoot, "name", "datum.json"),
            TryNum(datumRoot, "elevation_m"),
            StrOr(datumRoot, "elevation_frame", "未声明"),
            "datum.json",
            StrOr(datumRoot, "note", null));

        var ties = ParseTies(files, datumId);
        var sections = ParseSections(files, datumId);
        var boundaries = ParseBoundaries(files, datumId);
        var offsets = ParseOffsets(files);
        var events = ParseEvents(files, datumId);
        var anchors = ParseAnchors(files, datumId, offsets, events);
        var exclusions = ParseExclusions(files);

        SemanticChecks(datumId, sections, boundaries, events, anchors, exclusions, offsets);

        var status = _diag.Any(d => d.Severity == Severity.Error)
            ? ImportStatus.SemanticallyUnsolvable
            : ImportStatus.Success;

        var bundle = new MaterialBundle("bundle-" + digest[..12], datum, ties, sections, boundaries,
            events, anchors, exclusions, offsets);
        return status == ImportStatus.Success
            ? ImportResult.Ok(bundle, _diag, digest)
            : ImportResult.Fail(status, _diag, digest);
    }

    private JsonDocument? ParseDoc(string file, string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            Error(new SourcePosition(file, null, null), "format/json", "JSON 格式损坏：" + ex.Message);
            return null;
        }
    }

    private void Error(SourcePosition pos, string code, string msg) =>
        _diag.Add(new ImportDiagnostic(Severity.Error, pos, code, msg));
    private void Warn(SourcePosition pos, string code, string msg) =>
        _diag.Add(new ImportDiagnostic(Severity.Warning, pos, code, msg));

    private static string Str(JsonElement el, string prop, string file)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!
            : throw new KeyNotFoundException($"文件 {file} 缺少字符串字段 {prop}");

    private static string? StrOr(JsonElement el, string prop, string? fallback)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : fallback;

    private static double? TryNum(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)
            ? v : null;

    private static TEnum ParseEnum<TEnum>(JsonElement el, string prop, string file) where TEnum : struct
    {
        var s = Str(el, prop, file);
        if (!Enum.TryParse<TEnum>(s, ignoreCase: true, out var v))
            throw new FormatException($"文件 {file} 的 {prop}={s} 不是合法枚举");
        return v;
    }

    private static string ComputeDigest(IReadOnlyDictionary<string, string> files)
    {
        var sb = new StringBuilder();
        foreach (var kv in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('\0').Append(kv.Value).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private List<JsonElement> LoadArray(string file, string content, string code)
    {
        var list = new List<JsonElement>();
        var doc = ParseDoc(file, content);
        if (doc is null) throw new AbortImport();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            Error(new SourcePosition(file, null, null), "format/array", "顶层必须是数组");
            throw new AbortImport();
        }
        var limit = Limits.FirstOrDefault(l => l.File == file);
        if (limit is not null && doc.RootElement.GetArrayLength() > limit.Max)
        {
            Error(new SourcePosition(file, null, null), limit.Code,
                $"{limit.Message}（上限 {limit.Max}，实际 {doc.RootElement.GetArrayLength()}）");
            throw new AbortBudget();
        }
        foreach (var el in doc.RootElement.EnumerateArray()) list.Add(el);
        return list;
    }

    private sealed class AbortImport : Exception { }
    private sealed class AbortBudget : Exception { }
