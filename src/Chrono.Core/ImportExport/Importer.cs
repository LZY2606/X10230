using System.Text.Json;
using Chrono.Core.Model;
using Chrono.Core.Normalization;

namespace Chrono.Core.ImportExport;

public sealed class Importer
{
    private readonly List<Diagnostic> _diag = new();

    public static ImportOutcome Import(string jsonText)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException ex)
        {
            return new ImportOutcome("format_corrupt", null, Array.Empty<Diagnostic>(),
                $"JSON parse failed at {ex.BytePositionInLine?.ToString() ?? "?"}: {ex.Message}");
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new ImportOutcome("format_corrupt", null, Array.Empty<Diagnostic>(), "root is not an object");

        var importer = new Importer();
        return importer.Build(doc);
    }

    private ImportOutcome Build(JsonDocument doc)
    {
        var root = doc.RootElement;
        var coreRaw = Obj(root, "core", out var hasCore);
        if (!hasCore) { Add("CORE_MISSING", "core object is required", null, "$.core", null); return Incomplete(); }

        var coreId = Req(coreRaw, "id", "core");
        var name = Req(coreRaw, "name", "core");
        var projectCode = Req(coreRaw, "projectCode", "core");
        var depthDatumRaw = Req(coreRaw, "depthDatum", "core");
        var directionRaw = Req(coreRaw, "positiveDirection", "core");

        var depthDatumCode = NormalizeDatum(depthDatumRaw);
        var positive = NormalizeDirection(directionRaw);

        var top = Units.ParseDepth(Str(coreRaw, "topDepth"), coreId, "core.topDepth");
        var total = Units.ParseDepth(Str(coreRaw, "totalLength"), coreId, "core.totalLength");
        Record(top, coreId, "core.topDepth");
        Record(total, coreId, "core.totalLength");

        var core = new CoreInfo(
            Stable("core", coreId), name, projectCode,
            depthDatumRaw, depthDatumCode, directionRaw, positive,
            Str(coreRaw, "topDepth") ?? "", top.Value,
            Str(coreRaw, "totalLength") ?? "", total.Value,
            Source(root), Frag(coreRaw));

        var segments = MapSegments(root);
        var boundaries = MapBoundaries(root, segments);
        var reservoirs = MapReservoirs(root, segments);
        var eventLayers = MapEventLayers(root);
        var exclusions = MapExclusions(root);
        var anchors = MapAnchors(root, exclusions);

        var materials = new ProjectMaterials(
            Str(root, "schemaVersion") ?? "1.0",
            core, segments, boundaries, anchors, reservoirs, eventLayers, exclusions,
            _diag.ToArray())
        { MaterialsVersion = MaterialsHasher.Compute(root) };

        var hasError = _diag.Any(d => d.Severity == Severity.Error);
        return new ImportOutcome(hasError ? "incomplete" : "ok", materials, _diag.ToArray(), null);
    }

    private static JsonElement Obj(JsonElement root, string name, out bool found)
    {
        found = root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object;
        return found ? el : default;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private string Req(JsonElement el, string name, string where)
    {
        var v = Str(el, name);
        if (string.IsNullOrWhiteSpace(v))
            Add("FIELD_EMPTY", $"required field '{where}.{name}' is empty", null, $"$.{where}.{name}", null);
        return v ?? "";
    }

    private void Record(Measure m, string? mid, string field)
    {
        if (m.ErrorCode is not null)
            Add(m.ErrorCode, m.ErrorDetail ?? m.ErrorCode, mid, field, m.Raw);
    }

    private void Add(string code, string msg, string? mid, string? field, string? frag, Severity sev = Severity.Error)
        => _diag.Add(new Diagnostic(code, msg, mid, field, frag) { Severity = sev });

    private ImportOutcome Incomplete()
        => new("incomplete", null, _diag.ToArray(), null);

    private static string Source(JsonElement root) => Str(root, "source") ?? "case-pack";

    private static string Frag(JsonElement el) => el.GetRawText();

    private static string Stable(string kind, string localId) => $"{kind}_{Canonical.Fnv1a12(localId)}";

    private string NormalizeDatum(string raw)
    {
        var up = raw.Trim().ToUpperInvariant();
        return up switch
        {
            "MBSF" or "METERS BELOW SEAFLOOR" => "MBSF",
            "MCD" => "MCD",
            "CM BELOW SEAFLOOR" or "CMBSF" => "CMBSF",
            _ => raw
        };
    }

    private string NormalizeDirection(string raw)
    {
        var up = raw.Trim().ToLowerInvariant();
        if (up.Contains("down") || up.Contains("depth increases")) return "down-positive";
        if (up.Contains("up")) return "up-positive";
        Add("DIRECTION_UNKNOWN", $"unknown depth direction '{raw}'", null, "core.positiveDirection", raw, Severity.Warning);
        return raw;
    }
