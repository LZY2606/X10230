using System.Text.Json;
using DepthAgeReview.Domain;

namespace DepthAgeReview.Import;

/// <summary>
/// 领域片段导入器（JSON，schema depth-age-fragments/v1）。
/// 设计约束：
///  - 原始表示全部保留（*Raw 字段、单位字符串），规范化只发生在派生层；
///  - 无法解析的 JSON/缺必需字段 → <see cref="BuildOutcomeKind.FormatCorrupt"/>；
///  - 可解析但无法转换的值留在原位置并附位置诊断，绝不丢值/补默认；
///  - 诊断位置形如 fragment:anchors.json#line=4。
/// </summary>
public sealed class FragmentImporter
{
    private static readonly JsonDocumentOptions DocOpts = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public sealed record ParseReport(
        BuildOutcomeKind Outcome,
        MaterialBundle? Bundle,
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyDictionary<string, string> RawFragments);

    public ParseReport Parse(string version, IReadOnlyDictionary<string, string> fragments)
    {
        var diags = new List<Diagnostic>();
        DepthDatum? datum = null;
        var segments = new List<CoreSegment>();
        var anchors = new List<AgeAnchor>();
        var reservoirs = new List<ReservoirCorrection>();
        var events = new List<EventLayer>();
        var exclusions = new List<Exclusion>();
        var corrupt = false;

        foreach (var (name, text) in fragments)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text, DocOpts);
            }
            catch (JsonException ex)
            {
                corrupt = true;
                diags.Add(new Diagnostic("FRAGMENT_FORMAT_CORRUPT",
                    $"片段 {name} 不是合法 JSON：{ex.Message}", DiagnosticSeverity.Error,
                    $"{name}#line={(ex.LineNumber ?? 0) + 1}"));
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    corrupt = true;
                    diags.Add(new Diagnostic("FRAGMENT_FORMAT_CORRUPT", $"片段 {name} 根必须是对象", DiagnosticSeverity.Error, $"{name}#line=1"));
                    continue;
                }
                if (root.TryGetProperty("schema", out var sch) && sch.GetString() is { } s && s != "depth-age-fragments/v1")
                    diags.Add(new Diagnostic("SCHEMA_UNKNOWN", $"未知 schema '{s}'，按 v1 尽力解析", DiagnosticSeverity.Warning, $"{name}"));

                if (root.TryGetProperty("datum", out var d)) ReadDatum(d, ref datum, diags, name);
                foreach (var el in Array(root, "segments")) segments.Add(ReadSegment(name, text, el, diags, ref corrupt));
                foreach (var el in Array(root, "reservoirs")) reservoirs.Add(ReadReservoir(name, text, el, diags, ref corrupt));
                foreach (var el in Array(root, "anchors")) anchors.Add(ReadAnchor(name, text, el, diags, ref corrupt));
                foreach (var el in Array(root, "events")) events.Add(ReadEvent(name, text, el, diags, ref corrupt));
                foreach (var el in Array(root, "exclusions")) exclusions.Add(ReadExclusion(name, text, el, diags, ref corrupt));
            }
        }

        if (corrupt || datum is null)
        {
            if (datum is null && !corrupt)
                diags.Add(new Diagnostic("DATUM_MISSING", "缺少深度基准 datum（深度坐标没有基准，无法建模）", DiagnosticSeverity.Error, "fragment:*"));
            return new ParseReport(BuildOutcomeKind.FormatCorrupt, null, diags, fragments);
        }

        var (seg2, anc2) = CrossValidate(segments, anchors, reservoirs, events, exclusions, datum, diags);
        var bundle = new MaterialBundle(version, datum, seg2, anc2, reservoirs, events, exclusions);
        return new ParseReport(
            diags.Any(d => d.Severity == DiagnosticSeverity.Error) ? BuildOutcomeKind.SemanticUnresolvable : BuildOutcomeKind.Ok,
            bundle, diags, fragments);
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static (List<CoreSegment>, List<AgeAnchor>) CrossValidate(
        List<CoreSegment> segments, List<AgeAnchor> anchors,
        IReadOnlyList<ReservoirCorrection> reservoirs, IReadOnlyList<EventLayer> events,
        IReadOnlyList<Exclusion> exclusions, DepthDatum datum, List<Diagnostic> diags)
    {
        var segIds = new HashSet<string>(segments.Select(s => s.Id));
        var ancIds = new HashSet<string>(anchors.Select(a => a.Id));

        foreach (var s in segments)
        {
            var top = Quantity.ParseDepth(s.TopDepthRaw, s.DepthUnit, datum);
            var bot = Quantity.ParseDepth(s.BottomDepthRaw, s.DepthUnit, datum);
            if (!top.Convertible) diags.Add(new Diagnostic("DEPTH_UNCONVERTIBLE", top.Diagnostic!, DiagnosticSeverity.Error, $"segment:{s.Id}", s.Id));
            if (!bot.Convertible) diags.Add(new Diagnostic("DEPTH_UNCONVERTIBLE", bot.Diagnostic!, DiagnosticSeverity.Error, $"segment:{s.Id}", s.Id));
            if (top.Canonical is { } t && bot.Canonical is { } b && t >= b)
                diags.Add(new Diagnostic("SEGMENT_ENDPOINT_ORDER",
                    $"分段 {s.Id} 顶端深度 {t}cm 不小于底端 {b}cm（深度方向应为顶浅底深）",
                    DiagnosticSeverity.Error, $"segment:{s.Id}", s.Id));
        }
        foreach (var a in anchors)
        {
            var depth = Quantity.ParseDepth(a.DepthRaw, a.DepthUnit, datum);
            if (!depth.Convertible) diags.Add(new Diagnostic("DEPTH_UNCONVERTIBLE", depth.Diagnostic!, DiagnosticSeverity.Error, $"anchor:{a.Id}", a.Id));
            if (!segIds.Contains(a.SegmentId))
                diags.Add(new Diagnostic("SEGMENT_REF_MISSING", $"锚点 {a.Id} 指向不存在的分段 {a.SegmentId}", DiagnosticSeverity.Error, $"anchor:{a.Id}", a.Id));
            if (a.ReservoirCorrectionId is not null && !reservoirs.Any(r => r.Id == a.ReservoirCorrectionId))
                diags.Add(new Diagnostic("RESERVOIR_REF_MISSING", $"锚点 {a.Id} 引用不存在的储层偏移 {a.ReservoirCorrectionId}",
                    DiagnosticSeverity.Warning, $"anchor:{a.Id}", a.Id));
        }
        foreach (var e in events)
        {
            var depth = Quantity.ParseDepth(e.DepthRaw, e.DepthUnit, datum);
            if (!depth.Convertible) diags.Add(new Diagnostic("DEPTH_UNCONVERTIBLE", depth.Diagnostic!, DiagnosticSeverity.Error, $"event:{e.Id}", e.Id));
            if (!segIds.Contains(e.SegmentId))
                diags.Add(new Diagnostic("SEGMENT_REF_MISSING", $"事件层 {e.Id} 指向不存在的分段 {e.SegmentId}", DiagnosticSeverity.Error, $"event:{e.Id}", e.Id));
        }
        foreach (var x in exclusions)
            if (!ancIds.Contains(x.TargetAnchorId))
                diags.Add(new Diagnostic("EXCLUSION_TARGET_MISSING", $"排除决定 {x.Id} 指向不存在的锚点 {x.TargetAnchorId}",
                    DiagnosticSeverity.Error, $"exclusion:{x.Id}", x.Id));

        var active = exclusions.Where(x => x.Active).ToDictionary(x => x.TargetAnchorId, x => x.Id);
        var newAnchors = anchors.Select(a => a with
        {
            Depth = Quantity.ParseDepth(a.DepthRaw, a.DepthUnit, datum),
            Excluded = active.ContainsKey(a.Id),
            ExclusionDecisionId = active.GetValueOrDefault(a.Id),
        }).ToList();
        var newSegments = segments.Select(s => s with
        {
            Top = Quantity.ParseDepth(s.TopDepthRaw, s.DepthUnit, datum),
            Bottom = Quantity.ParseDepth(s.BottomDepthRaw, s.DepthUnit, datum),
        }).ToList();
        return (newSegments, newAnchors);
    }

    private static string Pos(string fragment, string raw, string? id)
    {
        var line = 1;
        if (id is not null)
        {
            var idx = raw.IndexOf(id, StringComparison.Ordinal);
            if (idx >= 0) line = raw[..idx].Count(c => c == '\n') + 1;
        }
        return $"{fragment}#line={line}";
    }

    private static void Require(JsonElement el, string field, string fragment, string raw, string? id, List<Diagnostic> diags, ref bool corrupt)
    {
        if (!el.TryGetProperty(field, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (p.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.GetString())))
        {
            corrupt = true;
            diags.Add(new Diagnostic("FIELD_MISSING", $"对象缺少必需字段 '{field}'", DiagnosticSeverity.Error, Pos(fragment, raw, id), id));
        }
    }

    private static void ReadDatum(JsonElement el, ref DepthDatum? datum, List<Diagnostic> diags, string fragment)
    {
        if (datum is not null)
            diags.Add(new Diagnostic("DATUM_DUPLICATE", $"片段 {fragment} 再次提供深度基准，忽略后者", DiagnosticSeverity.Warning, fragment));
        else
            datum = new DepthDatum(Str(el, "id", "datum-1")!, Str(el, "name", "深度基准"), Str(el, "reference", "core top")!,
                Str(el, "depthUnit", "cm")!, Str(el, "note"));
    }

    private static CoreSegment ReadSegment(string fragment, string raw, JsonElement el, List<Diagnostic> diags, ref bool corrupt)
    {
        var id = Str(el, "id");
        Require(el, "id", fragment, raw, id, diags, ref corrupt);
        Require(el, "top", fragment, raw, id, diags, ref corrupt);
        Require(el, "bottom", fragment, raw, id, diags, ref corrupt);
        return new CoreSegment(id ?? "?", Str(el, "top", "")!, Str(el, "bottom", "")!, Str(el, "depthUnit", "cm")!,
            el.TryGetProperty("conformable", out var cf) && cf.GetBoolean(),
            Num(el, "gapToTopCm") ?? 0, Str(el, "lithology", "")!, Str(el, "note"));
    }

    private static ReservoirCorrection ReadReservoir(string fragment, string raw, JsonElement el, List<Diagnostic> diags, ref bool corrupt)
    {
        var id = Str(el, "id");
        Require(el, "id", fragment, raw, id, diags, ref corrupt);
        return new ReservoirCorrection(id ?? "?", Str(el, "name", "储层偏移"),
            Num(el, "deltaCalAbp") ?? 0, Num(el, "sigmaCalAbp") ?? 0, Str(el, "segmentId"), Str(el, "note"));
    }

    private static AgeAnchor ReadAnchor(string fragment, string raw, JsonElement el, List<Diagnostic> diags, ref bool corrupt)
    {
        var id = Str(el, "id");
        foreach (var f in new[] { "id", "segmentId", "depth", "kind" }) Require(el, f, fragment, raw, id, diags, ref corrupt);
        if (!el.TryGetProperty("kind", out var k) || !Enum.TryParse<AgeDistributionKind>(k.GetString(), true, out _))
        {
            corrupt = true;
            diags.Add(new Diagnostic("FIELD_BAD_ENUM", $"锚点 {id} 的 age 分布 kind 非法（允许 Normal/StudentT/Empirical/Point）",
                DiagnosticSeverity.Error, Pos(fragment, raw, id), id));
        }
        double[]? samples = null;
        if (el.TryGetProperty("samples", out var sp))
        {
            if (sp.ValueKind == JsonValueKind.Array) samples = sp.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            else if (sp.ValueKind == JsonValueKind.String)
            {
                try { samples = EmpiricalDist.ParseCsv(sp.GetString()!).Sorted.ToArray(); }
                catch (FormatException)
                {
                    corrupt = true;
                    diags.Add(new Diagnostic("SAMPLES_FORMAT_CORRUPT", $"锚点 {id} 的 samples 文本含非数值",
                        DiagnosticSeverity.Error, Pos(fragment, raw, id), id));
                }
            }
        }
        return new AgeAnchor(id ?? "?", Str(el, "label", id ?? "锚点")!, Str(el, "segmentId", "")!,
            Str(el, "depth", "")!, Str(el, "depthUnit", "cm")!,
            Enum.TryParse<AgeDistributionKind>(Str(el, "kind"), true, out var kk) ? kk : AgeDistributionKind.Normal,
            Str(el, "ageUnit", "cal a BP")!, Num(el, "mean"), Num(el, "sigma"), Num(el, "nu"),
            samples, Str(el, "reservoirCorrectionId"), Str(el, "datingMethod", "")!, Str(el, "note"));
    }

    private static EventLayer ReadEvent(string fragment, string raw, JsonElement el, List<Diagnostic> diags, ref bool corrupt)
    {
        var id = Str(el, "id");
        foreach (var f in new[] { "id", "segmentId", "depth" }) Require(el, f, fragment, raw, id, diags, ref corrupt);
        return new EventLayer(id ?? "?", Str(el, "label", id ?? "事件层")!, Str(el, "segmentId", "")!,
            Str(el, "depth", "")!, Str(el, "depthUnit", "cm")!, Str(el, "correlatedEvent"),
            Num(el, "knownAgeCalAbp"), Num(el, "knownAgeSigmaCalAbp"), Str(el, "note"));
    }

    private static Exclusion ReadExclusion(string fragment, string raw, JsonElement el, List<Diagnostic> diags, ref bool corrupt)
    {
        var id = Str(el, "id");
        foreach (var f in new[] { "id", "targetAnchorId", "reason" }) Require(el, f, fragment, raw, id, diags, ref corrupt);
        return new Exclusion(id ?? "?", Str(el, "targetAnchorId", "")!, Str(el, "reason", "")!, (int)(Num(el, "decisionSeq") ?? 0));
    }

    private static string? Str(JsonElement el, string field, string? def = null) =>
        el.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : def;

    private static double? Num(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.String) return null;
        try { return p.GetDouble(); } catch { return null; }
    }
}
