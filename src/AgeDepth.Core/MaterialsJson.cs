using System.Text.Json;

namespace AgeDepth.Core;

/// <summary>
/// 材料 JSON → 实体。解析失败 = FormatCorrupt（带位置）；
/// 原始表示保留在 RawQuantity，规范化在派生层完成。
/// </summary>
public static class MaterialsJson
{
    public static MaterialsBundle Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatCorruptException($"JSON 解析失败: {ex.Message}", "$"); }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatCorruptException("材料包必须是 JSON 对象", "$");

        var sections = new List<CoreSection>();
        var boundaries = new List<Boundary>();
        var anchors = new List<AgeAnchor>();
        var layers = new List<EventLayer>();

        if (root.TryGetProperty("sections", out var ss))
        {
            var i = 0;
            foreach (var s in ss.EnumerateArray())
            {
                var pos = $"$.sections[{i}]";
                sections.Add(new CoreSection(Req(s, "id", pos), Opt(s, "label") ?? "",
                    Qty(s, "top", QuantityKind.Depth, pos), Qty(s, "bottom", QuantityKind.Depth, pos)));
                i++;
            }
        }
        if (root.TryGetProperty("boundaries", out var bs))
        {
            var i = 0;
            foreach (var b in bs.EnumerateArray())
            {
                var pos = $"$.boundaries[{i}]";
                boundaries.Add(new Boundary(Req(b, "id", pos),
                    Qty(b, "depth", QuantityKind.Depth, pos), Req(b, "kind", pos),
                    b.TryGetProperty("depthTo", out var dt) ? Qty(b, "depthTo", QuantityKind.Depth, pos) : null,
                    Opt(b, "note")));
                i++;
            }
        }
        if (root.TryGetProperty("anchors", out var ans))
        {
            var i = 0;
            foreach (var a in ans.EnumerateArray())
            {
                var pos = $"$.anchors[{i}]";
                var id = Req(a, "id", pos);
                anchors.Add(new AgeAnchor(id,
                    Qty(a, "depth", QuantityKind.Depth, pos),
                    Dist(a, pos),
                    a.TryGetProperty("reservoirOffset", out _)
                        ? Qty(a, "reservoirOffset", QuantityKind.Duration, pos) : null,
                    Opt(a, "source") ?? "unknown",
                    Opt(a, "status") ?? "accepted",
                    Opt(a, "exclusionReason")));
                i++;
            }
        }
        if (root.TryGetProperty("eventLayers", out var els))
        {
            var i = 0;
            foreach (var e in els.EnumerateArray())
            {
                var pos = $"$.eventLayers[{i}]";
                layers.Add(new EventLayer(Req(e, "id", pos),
                    Qty(e, "depth", QuantityKind.Depth, pos),
                    Req(e, "correlatesTo", pos), Opt(e, "note")));
                i++;
            }
        }
        return new MaterialsBundle(sections, boundaries, anchors, layers);
    }

    private static string Req(JsonElement el, string name, string pos) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatCorruptException($"缺少字符串字段 '{name}'", $"{pos}.{name}");

    private static string? Opt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static RawQuantity Qty(JsonElement el, string name, QuantityKind kind, string pos)
    {
        if (!el.TryGetProperty(name, out var q) || q.ValueKind != JsonValueKind.Object)
            throw new FormatCorruptException($"缺少量值对象 '{name}'", $"{pos}.{name}");
        var raw = q.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()!
            : throw new FormatCorruptException($"量值缺少 'raw' 原始表示", $"{pos}.{name}.raw");
        var unit = q.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var datum = q.TryGetProperty("datum", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return RawQuantity.Create(raw, unit, datum, kind, $"{pos}.{name}");
    }

    private static AgeDist Dist(JsonElement a, string pos)
    {
        if (!a.TryGetProperty("age", out var d) || d.ValueKind != JsonValueKind.Object)
            throw new FormatCorruptException("锚点缺少 'age' 分布对象", $"{pos}.age");
        var kind = d.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()!
            : throw new FormatCorruptException("年龄分布缺少 'kind'", $"{pos}.age.kind");
        double? Num(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
        double[]? samples = null;
        if (d.TryGetProperty("samples", out var sv) && sv.ValueKind == JsonValueKind.Array)
            samples = sv.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new AgeDist
        {
            Kind = kind, Samples = samples,
            Mean = Num("mean"), Sd = Num("sd"), Min = Num("min"), Max = Num("max"),
            Unit = d.TryGetProperty("unit", out var uu) && uu.ValueKind == JsonValueKind.String
                ? uu.GetString()! : "ka",
            Note = d.TryGetProperty("note", out var nn) && nn.ValueKind == JsonValueKind.String
                ? nn.GetString() : null
        };
    }
}
