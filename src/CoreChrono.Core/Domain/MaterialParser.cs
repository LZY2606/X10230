using System.Text.Json;

namespace CoreChrono.Core.Domain;

/// <summary>
/// 三类失败严格区分：
///  CorruptFormat：JSON 本身损坏（无法解析）；
///  SemanticInvalid：结构可解析但领域语义不成立（未知 kind、缺字段）；
///  Imported：成功，可能带诊断（无法转换的值留在原位置）。
/// </summary>
public sealed record ParseResult(ImportedMaterial? Material, bool CorruptFormat, bool SemanticInvalid, Diagnostic? Fatal);

public static class MaterialParser
{
    public static ParseResult Parse(string rawJson, string sourcePath)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawJson); }
        catch (JsonException ex)
        {
            return new ParseResult(null, true, false,
                Diagnostic.Error("material.corrupt-format", sourcePath, $"JSON 损坏：{ex.Message}"));
        }
        using (doc)
        {
            var root = doc.RootElement;
            var diags = new List<Diagnostic>();
            if (root.ValueKind != JsonValueKind.Object)
                return new ParseResult(null, false, true,
                    Diagnostic.Error("material.not-object", sourcePath, "材料文件根节点必须是对象"));

            var kind = root.TryGetProperty("kind", out var kp) ? kp.GetString() : null;
            if (string.IsNullOrWhiteSpace(kind))
                return new ParseResult(null, false, true,
                    Diagnostic.Error("material.kind-missing", sourcePath, "缺少 kind 字段，无法确定材料类型"));

            var id = root.TryGetProperty("id", out var ip) ? ip.GetString() : null;
            var hash = StableIds.Hash(rawJson);
            if (string.IsNullOrWhiteSpace(id)) id = $"mat-{hash[..10]}";

            object? payload = kind switch
            {
                "depth-datum" => ParseDepthDatum(root, id!, sourcePath, diags),
                "segment-gap" => ParseGap(root, id!, sourcePath, diags),
                "radiometric-age" => ParseRadiometric(root, id!, sourcePath, diags),
                "reservoir-offset" => ParseReservoir(root, id!, sourcePath, diags),
                "event-bed" => ParseEventBed(root, id!, sourcePath, diags),
                "exclusion-decision" => ParseExclusion(root, id!),
                _ => null
            };
            if (payload is null)
                return new ParseResult(null, false, true,
                    Diagnostic.Error("material.unknown-kind", sourcePath, $"未知材料类型 '{kind}'"));

            var material = new ImportedMaterial(id!, kind, rawJson, hash,
                JsonSerializer.SerializeToElement(payload), diags);
            return new ParseResult(material, false, false, null);
        }
    }

    private static string? Str(this JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static DepthValue? ParseRef(JsonElement el, string field, string defaultDatum, string source, List<Diagnostic> diags)
    {
        if (!el.TryGetProperty(field, out var refEl))
        {
            diags.Add(Diagnostic.Error("depth.missing", $"{source}#{field}", $"缺少深度引用 {field}"));
            return null;
        }
        var raw = refEl.ValueKind == JsonValueKind.Object ? refEl.Str("raw") : refEl.GetString();
        var datum = refEl.ValueKind == JsonValueKind.Object
            ? (refEl.Str("datum") ?? defaultDatum) : defaultDatum;
        if (QuantityParser.TryParseDepth(raw, datum, out var v, out var d)) return v;
        diags.Add(d!);
        return null;
    }

    private static AgeUnit? MapUnit(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "cal yr bp" or "cal yr" or "calbp" => AgeUnit.CalYrBP,
        "yr bp" or "yr" or "years" => AgeUnit.YrBP,
        "ka bp" or "ka" => AgeUnit.KaBP,
        _ => null
    };

    public static AgeDistribution? ParseDistribution(JsonElement el, string source, List<Diagnostic> diags)
    {
        var dist = el.Str("dist");
        var unit = MapUnit(el.Str("unit"));
        if (unit is null)
        {
            diags.Add(Diagnostic.Error("age.unknown-unit", source, $"年龄分布单位 '{el.Str("unit")}' 无法识别，分布保留原始 JSON 但不参与计算"));
            return null;
        }
        var scale = el.Str("scale") ?? "unspecified";
        double Get(string n) => el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN;
        try
        {
            AgeDistribution d = dist switch
            {
                "normal" => new NormalAge(Get("mean"), Get("sigma"), unit.Value, scale),
                "uniform" => new UniformAge(Get("min"), Get("max"), unit.Value, scale),
                "longtail" => new LongTailAge(Get("mode"), Get("sigma"), Get("tailLambda"), unit.Value, scale),
                _ => null!
            };
            if (d is null || d.Describe().Contains("NaN"))
            {
                diags.Add(Diagnostic.Error("age.incomplete", source, $"年龄分布 '{dist}' 参数不完整，保留原始 JSON 不参与计算"));
                return null;
            }
            return d;
        }
        catch (Exception ex)
        {
            diags.Add(Diagnostic.Error("age.incomplete", source, $"年龄分布解析失败：{ex.Message}"));
            return null;
        }
    }

    private static DepthDatumPoint ParseDepthDatum(JsonElement r, string id, string src, List<Diagnostic> d) => new(
        id, r.Str("label") ?? id,
        r.TryGetProperty("depth", out var dep) ? (dep.Str("raw") ?? "") : "",
        r.TryGetProperty("depth", out _) ? ParseRef(r, "depth", r.Str("datumDefinition") ?? "core-top", src, d) : null,
        r.Str("datumDefinition") ?? "core-top", r.Str("note") ?? "", r.Str("sourceRef") ?? src);

    private static SegmentGap ParseGap(JsonElement r, string id, string src, List<Diagnostic> d) => new(
        id,
        r.TryGetProperty("top", out var t) ? (t.Str("raw") ?? "") : "",
        r.TryGetProperty("bottom", out var b) ? (b.Str("raw") ?? "") : "",
        r.TryGetProperty("top", out _) ? ParseRef(r, "top", r.Str("datum") ?? "core-top", src, d) : null,
        r.TryGetProperty("bottom", out _) ? ParseRef(r, "bottom", r.Str("datum") ?? "core-top", src, d) : null,
        r.Str("gapKind") ?? "unknown", r.Str("reason") ?? "", r.Str("sourceRef") ?? src);

    private static RadiometricAge ParseRadiometric(JsonElement r, string id, string src, List<Diagnostic> d)
    {
        AgeDistribution? dist = null;
        string rawAge = "";
        if (r.TryGetProperty("age", out var ageEl))
        {
            rawAge = ageEl.GetRawText();
            dist = ParseDistribution(ageEl, $"{src}#age", d);
        }
        else d.Add(Diagnostic.Error("age.missing", $"{src}#age", "缺少 age 分布"));
        return new RadiometricAge(id, r.Str("labCode") ?? id, r.Str("method") ?? "?",
            r.Str("material") ?? "",
            r.TryGetProperty("depth", out _) ? (r.GetProperty("depth").Str("raw") ?? "") : "",
            r.TryGetProperty("depth", out _) ? ParseRef(r, "depth", "core-top", src, d) : null,
            rawAge, dist, r.Str("sourceRef") ?? src);
    }

    private static ReservoirOffset ParseReservoir(JsonElement r, string id, string src, List<Diagnostic> d)
    {
        AgeValue? off = null;
        var raw = r.TryGetProperty("offset", out var oe) ? (oe.Str("raw") ?? "") : "";
        if (!string.IsNullOrEmpty(raw))
        {
            var unit = MapUnit(r.TryGetProperty("offset", out _) ? r.GetProperty("offset").Str("unit") : null) ?? AgeUnit.YrBP;
            var first = raw.Split(' ')[0];
            if (double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vv))
                off = new AgeValue(AgeUnits.ToCalYrBP(vv, unit), unit, vv, r.Str("rationale")?.Length > 0 ? "ΔR" : "ΔR");
            else d.Add(Diagnostic.Error("offset.unparseable", $"{src}#offset", $"储层偏移 '{raw}' 无法解析，保留原值"));
        }
        return new ReservoirOffset(id, r.Str("appliesToMethod") ?? "*",
            r.Str("appliesToMaterial") ?? "*", raw, off, r.Str("rationale") ?? "", r.Str("sourceRef") ?? src);
    }

    private static EventBed ParseEventBed(JsonElement r, string id, string src, List<Diagnostic> d)
    {
        AgeDistribution? corr = null;
        string? rawCorr = null;
        if (r.TryGetProperty("correlationAge", out var ce))
        {
            rawCorr = ce.GetRawText();
            corr = ParseDistribution(ce, $"{src}#correlationAge", d);
        }
        return new EventBed(id, r.Str("name") ?? id,
            r.TryGetProperty("depth", out _) ? (r.GetProperty("depth").Str("raw") ?? "") : "",
            r.TryGetProperty("depth", out _) ? ParseRef(r, "depth", "core-top", src, d) : null,
            r.Str("correlatesTo"), rawCorr, corr, r.Str("sourceRef") ?? src);
    }

    private static ExclusionDecision ParseExclusion(JsonElement r, string id) => new(
        id, r.Str("target") ?? "", r.Str("decision") ?? "excluded",
        r.Str("reason") ?? "", r.Str("supersedes"));
}
