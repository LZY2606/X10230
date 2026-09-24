using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>Parses the domain JSON bundle. Raw text is preserved; unconvertible values stay in place with diagnostics.</summary>
public static class BundleParser
{
    public static Outcome<MaterialBundle> Parse(string json, string sourceRef)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex)
        {
            return Outcome<MaterialBundle>.Corrupt(new[]
            {
                new Diagnostic(Severity.Error, "JSON_CORRUPT", "malformed JSON: " + ex.Message,
                    Line: ex.LineNumber, Snippet: Snippet(json, ex.LineNumber ?? 0))
            });
        }
        if (root is not JsonObject o)
            return Outcome<MaterialBundle>.Corrupt(new[]
            { new(Severity.Error, "JSON_ROOT", "root must be an object") });

        var diags = new List<Diagnostic>();

        string? bundleId = o["bundleId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(bundleId))
        {
            return Outcome<MaterialBundle>.Corrupt(new[]
            { new(Severity.Error, "BUNDLE_ID_MISSING", "bundleId is required", Field: "bundleId") });
        }

        var depthDatum = ParseDepthDatum(o["depthDatum"], diags);
        var timeDatum = ParseTimeDatum(o["timeDatum"], diags);
        if (depthDatum is null || timeDatum is null)
            return Outcome<MaterialBundle>.Corrupt(diags);

        var segments = ParseSegments(o["segments"], depthDatum, diags);
        var reservoirs = ParseReservoirs(o["reservoirs"], timeDatum, diags);
        var anchors = ParseAnchors(o["anchors"], depthDatum, timeDatum, diags);
        var events = ParseEvents(o["eventBeds"], depthDatum, timeDatum, diags);
        var exclusions = ParseExclusions(o["exclusions"], diags);

        var hasError = diags.Any(d => d.Severity == Severity.Error);
        var bundle = new MaterialBundle(bundleId, depthDatum, timeDatum, segments, anchors,
            reservoirs, events, exclusions, diags.Where(d => d.Severity != Severity.Error).ToList(), sourceRef);
        return hasError
            ? Outcome<MaterialBundle>.Unsolvable(diags.Append(new Diagnostic(Severity.Error,
                "BUNDLE_SEMANTIC", "bundle contains unconvertible values; retained in place", bundleId)).ToList())
            : Outcome<MaterialBundle>.Success(bundle, diags);
    }

    private static string? Snippet(string json, int line)
    {
        var lines = json.Replace("\r", "").Split('\n');
        return line >= 0 && line < lines.Length ? lines[line].Trim() : null;
    }

    private static DepthDatum? ParseDepthDatum(JsonNode? node, List<Diagnostic> diags)
    {
        if (node is not JsonObject o)
        { diags.Add(new(Severity.Error, "DEPTH_DATUM", "depthDatum required", Field: "depthDatum")); return null; }
        string id = o["id"]?.GetValue<string>() ?? "depth";
        return new DepthDatum(id, o["name"]?.GetValue<string>() ?? id,
            o["depthUnit"]?.GetValue<string>() ?? "cm", o["zeroDefinition"]?.GetValue<string>());
    }

    private static TimeDatum? ParseTimeDatum(JsonNode? node, List<Diagnostic> diags)
    {
        if (node is not JsonObject o)
        { diags.Add(new(Severity.Error, "TIME_DATUM", "timeDatum required", Field: "timeDatum")); return null; }
        string id = o["id"]?.GetValue<string>() ?? "time";
        return new TimeDatum(id, o["name"]?.GetValue<string>() ?? id,
            o["timeUnit"]?.GetValue<string>() ?? "yr BP", o["epochDefinition"]?.GetValue<string>());
    }

    private static IReadOnlyList<Segment> ParseSegments(JsonNode? node, DepthDatum datum, List<Diagnostic> diags)
    {
        var list = new List<Segment>();
        if (node is not JsonArray arr) { diags.Add(new(Severity.Error, "SEGMENTS", "segments array required", Field: "segments")); return list; }
        int order = 0;
        foreach (var item in arr)
        {
            if (item is not JsonObject s) { diags.Add(new(Severity.Error, "SEGMENT_ROW", "segment must be object", Line: order)); order++; continue; }
            string id = s["id"]?.GetValue<string>() ?? $"seg-{order + 1}";
            var top = UnitValue.Parse(s["topDepth"]?.GetValue<string>(), datum.DepthUnit);
            var bottom = UnitValue.Parse(s["bottomDepth"]?.GetValue<string>(), datum.DepthUnit);
            if (!top.IsConvertible) diags.Add(new(Severity.Warning, "DEPTH_UNCONVERTIBLE", "top depth kept as raw text", id, "topDepth", Snippet: top.Raw));
            if (!bottom.IsConvertible) diags.Add(new(Severity.Warning, "DEPTH_UNCONVERTIBLE", "bottom depth kept as raw text", id, "bottomDepth", Snippet: bottom.Raw));
            var cont = Enum.TryParse<SegmentContinuity>(s["continuity"]?.GetValue<string>()?.Replace("-", ""),
                ignoreCase: true, out var cv) ? cv : SegmentContinuity.Continuous;
            list.Add(new Segment(id, order, top, bottom, cont,
                s["lithology"]?.GetValue<string>() ?? "unknown", s["note"]?.GetValue<string>()));
            order++;
        }
        return list;
    }

    private static IReadOnlyList<ReservoirCorrection> ParseReservoirs(JsonNode? node, TimeDatum td, List<Diagnostic> diags)
    {
        var list = new List<ReservoirCorrection>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject r) continue;
            string id = r["id"]?.GetValue<string>() ?? $"res-{list.Count + 1}";
            var parsed = DistributionFactory.Parse(r["offset"], td.TimeUnit, id, "offset", null);
            diags.AddRange(parsed.Diagnostics);
            list.Add(new ReservoirCorrection(id, r["scope"]?.GetValue<string>() ?? "global",
                parsed.Distribution, r["appliesToAnchorId"]?.GetValue<string>()));
        }
        return list;
    }

    private static IReadOnlyList<AgeAnchor> ParseAnchors(JsonNode? node, DepthDatum dd, TimeDatum td, List<Diagnostic> diags)
    {
        var list = new List<AgeAnchor>();
        if (node is not JsonArray arr) { diags.Add(new(Severity.Error, "ANCHORS", "anchors array required", Field: "anchors")); return list; }
        foreach (var item in arr)
        {
            if (item is not JsonObject a) continue;
            string id = a["id"]?.GetValue<string>() ?? $"anc-{list.Count + 1}";
            var depth = UnitValue.Parse(a["depth"]?.GetValue<string>(), dd.DepthUnit);
            if (!depth.IsConvertible) diags.Add(new(Severity.Warning, "ANCHOR_DEPTH_UNCONVERTIBLE",
                "anchor depth kept as raw text", id, "depth", Snippet: depth.Raw));
            var parsed = DistributionFactory.Parse(a["age"], td.TimeUnit, id, "age", null);
            diags.AddRange(parsed.Diagnostics);
            bool excluded = a["excluded"]?.GetValue<bool>() ?? false;
            list.Add(new AgeAnchor(id, depth, parsed.Distribution,
                a["method"]?.GetValue<string>() ?? "unknown",
                a["materialType"]?.GetValue<string>(),
                excluded,
                a["exclusionReason"]?.GetValue<string>(),
                a["reservoirCorrectionId"]?.GetValue<string>()));
        }
        return list;
    }

    private static IReadOnlyList<EventBed> ParseEvents(JsonNode? node, DepthDatum dd, TimeDatum td, List<Diagnostic> diags)
    {
        var list = new List<EventBed>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject e) continue;
            string id = e["id"]?.GetValue<string>() ?? $"evt-{list.Count + 1}";
            var depth = UnitValue.Parse(e["depth"]?.GetValue<string>(), dd.DepthUnit);
            if (!depth.IsConvertible) diags.Add(new(Severity.Warning, "EVENT_DEPTH_UNCONVERTIBLE",
                "event depth kept as raw text", id, "depth", Snippet: depth.Raw));
            var parsed = DistributionFactory.Parse(e["correlationAge"], td.TimeUnit, id, "correlationAge", null);
            diags.AddRange(parsed.Diagnostics);
            list.Add(new EventBed(id, depth, e["label"]?.GetValue<string>() ?? id,
                parsed.Distribution, e["correlationBasis"]?.GetValue<string>() ?? "unrecorded"));
        }
        return list;
    }

    private static IReadOnlyList<ExclusionDecision> ParseExclusions(JsonNode? node, List<Diagnostic> diags)
    {
        var list = new List<ExclusionDecision>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            if (item is not JsonObject e) continue;
            list.Add(new ExclusionDecision(
                e["id"]?.GetValue<string>() ?? $"exc-{list.Count + 1}",
                e["targetId"]?.GetValue<string>() ?? "",
                e["targetKind"]?.GetValue<string>() ?? "anchor",
                e["reason"]?.GetValue<string>() ?? "",
                e["decidedBy"]?.GetValue<string>() ?? "import",
                e["retainedButExcluded"]?.GetValue<bool>() ?? true));
        }
        return list;
    }
}
