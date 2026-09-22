using System.Text.Json.Nodes;
using GeoChron.Domain;

namespace GeoChron.Derivation;

public sealed class NormalizedSet
{
    public string? Datum;
    public List<CoreSeg> Segments { get; } = new();
    public List<Gap> Gaps { get; } = new();
    public List<Reservoir> Reservoirs { get; } = new();
    public List<EventLayer> Events { get; } = new();
    public List<Anchor> Anchors { get; } = new();
    public List<Diag> Diags { get; } = new();
    public List<RawItem> ActiveMaterials { get; } = new();
}

/// <summary>Resolves active lineage versions and normalizes all source material.</summary>
public static class MaterialNormalizer
{
    public static NormalizedSet Normalize(IEnumerable<RawItem> materials)
    {
        var all = materials.OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        var set = new NormalizedSet();

        // Latest version per lineage wins; superseded originals are kept in storage but inactive.
        var active = new Dictionary<string, RawItem>();
        foreach (var m in all)
        {
            var key = m.Lineage ?? m.Id;
            if (!active.TryGetValue(key, out var prev) || m.Order >= prev.Order) active[key] = m;
        }
        set.ActiveMaterials.AddRange(active.Values.OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal));

        var n = new Normalizer();
        foreach (var m in set.ActiveMaterials)
        {
            var loc = $"{m.Source}#{m.Order}:{m.Type}:{m.Lineage ?? m.Id}";
            var r = m.Raw;
            try
            {
                switch (m.Type)
                {
                    case "depth-datum":
                        set.Datum ??= r["datum"]?.GetValue<string>();
                        if (set.Datum is null) n.Diags.Add(Diag.Error("DATUM_MISSING", loc, "depth datum has no datum name"));
                        break;
                    case "segment": ParseSegment(n, set, m, loc); break;
                    case "section-gap": ParseGap(n, set, m, loc); break;
                    case "reservoir": ParseReservoir(n, set, m, loc); break;
                    case "event-layer": ParseEvent(n, set, m, loc); break;
                    case "age-measurement": ParseAge(n, set, m, loc); break;
                    default:
                        n.Diags.Add(Diag.Warn("MATERIAL_TYPE_UNKNOWN", loc, $"unknown material type '{m.Type}' retained as-is"));
                        break;
                }
            }
            catch (Exception ex)
            {
                n.Diags.Add(Diag.Error("FRAGMENT_CORRUPT", loc, $"fragment could not be parsed: {ex.GetType().Name}"));
            }
        }

        ApplyReservoirs(n, set);
        OrderChecks(n, set);
        set.Diags.AddRange(n.Diags);
        return set;
    }

    static void ParseSegment(Normalizer n, NormalizedSet set, RawItem m, string loc)
    {
        var r = m.Raw;
        var top = n.ParseDepth(new JsonObject { ["depth"] = r["topDepth"]?.DeepClone(), ["datum"] = r["datum"]?.DeepClone() }, loc + ":topDepth");
        var bottom = n.ParseDepth(new JsonObject { ["depth"] = r["bottomDepth"]?.DeepClone(), ["datum"] = r["datum"]?.DeepClone() }, loc + ":bottomDepth");
        var kind = (r["kind"]?.GetValue<string>() ?? "normal").ToLowerInvariant() switch
        {
            "inverted" => SegmentKind.Inverted,
            "faulted" => SegmentKind.Faulted,
            _ => SegmentKind.Normal
        };
        var gapKind = BoundaryKind.None;
        var gapLineage = r["gapToNext"]?.GetValue<string>();
        if (r["boundaryToNext"] is not null)
            gapKind = ParseBoundary(r["boundaryToNext"]!.GetValue<string>());
        set.Segments.Add(new CoreSeg(
            m.Lineage ?? m.Id,
            r["name"]?.GetValue<string>() ?? m.Lineage ?? m.Id,
            r["lithology"]?.GetValue<string>() ?? "",
            top, bottom, kind, gapLineage, gapKind));
    }

    static void ParseGap(Normalizer n, NormalizedSet set, RawItem m, string loc)
    {
        var r = m.Raw;
        var kind = ParseBoundary(r["kind"]?.GetValue<string>() ?? "hiatus");
        DepthPoint? upper = null, lower = null;
        if (r["upperDepth"] is not null)
            upper = n.ParseDepth(new JsonObject { ["depth"] = r["upperDepth"]?.DeepClone(), ["datum"] = r["datum"]?.DeepClone() }, loc + ":upperDepth");
        if (r["lowerDepth"] is not null)
            lower = n.ParseDepth(new JsonObject { ["depth"] = r["lowerDepth"]?.DeepClone(), ["datum"] = r["datum"]?.DeepClone() }, loc + ":lowerDepth");
        double? mg = null, dur = null;
        if (upper is { Convertible: true } && lower is { Convertible: true }) mg = lower.Metres - upper.Metres;
        if (r["durationMinYears"] is JsonValue dv && dv.TryGetValue<double>(out var d)) dur = d;
        set.Gaps.Add(new Gap(
            m.Lineage ?? m.Id,
            r["name"]?.GetValue<string>() ?? m.Lineage ?? m.Id,
            kind, upper, lower,
            r["upperSegment"]?.GetValue<string>(), r["lowerSegment"]?.GetValue<string>(),
            mg, dur));
    }

    static void ParseReservoir(Normalizer n, NormalizedSet set, RawItem m, string loc)
    {
        var r = m.Raw;
        if (!TryD(r["deltaR"], out var dr) || !TryD(r["sigmaYears"], out var sg))
        {
            n.Diags.Add(Diag.Error("RESERVOIR_INCOMPLETE", loc, "reservoir requires numeric deltaR and sigmaYears"));
            return;
        }
        var kind = (r["kind"]?.GetValue<string>() ?? "global-average").ToLowerInvariant() switch
        {
            "marine-delta-r" => ReservoirKind.MarineDeltaR,
            "local-estimate" => ReservoirKind.LocalEstimate,
            _ => ReservoirKind.GlobalAverage
        };
        set.Reservoirs.Add(new Reservoir(m.Lineage ?? m.Id, r["name"]?.GetValue<string>() ?? "", kind, dr, sg, m.Source));
    }

    static void ParseEvent(Normalizer n, NormalizedSet set, RawItem m, string loc)
    {
        var r = m.Raw;
        var depth = n.ParseDepth(r, loc + ":depth");
        bool used = r["useAsAnchor"]?.GetValue<bool?>() ?? false;
        bool excluded = r["excluded"]?.GetValue<bool?>() ?? false;
        double? age = null, sig = null;
        if (r["anchorAge14CBP"] is JsonValue av && av.TryGetValue<double>(out var aa)) age = aa;
        if (r["anchorSigmaYears"] is JsonValue sv && sv.TryGetValue<double>(out var ss)) sig = ss;
        if (used && excluded)
            n.Diags.Add(Diag.Warn("EXCLUDE_CONFLICT", loc, "event layer both used as anchor and excluded; exclusion wins"));
        if (used && (!excluded) && (!age.HasValue || !sig.HasValue || !depth.Convertible))
        {
            n.Diags.Add(Diag.Warn("EVENT_ANCHOR_INCOMPLETE", loc, "event layer cannot anchor without depth, age and sigma; retained, not used"));
            used = false;
        }
        set.Events.Add(new EventLayer(
            m.Lineage ?? m.Id, r["name"]?.GetValue<string>() ?? "",
            r["marker"]?.GetValue<string>() ?? "", r["correlation"]?.GetValue<string>() ?? "",
            depth, used && !excluded, excluded, r["excludeReason"]?.GetValue<string>(),
            age, sig));
    }

    static void ParseAge(Normalizer n, NormalizedSet set, RawItem m, string loc)
    {
        var r = m.Raw;
        var excluded = r["excluded"]?.GetValue<bool?>() ?? false;
        var reason = r["excludeReason"]?.GetValue<string>();
        var depth = n.ParseDepth(r, loc + ":depth");
        var method = r["method"]?.GetValue<string>() ?? "14C";
        var basisRaw = r["ageBasis"]?.GetValue<string>() ?? "";
        var (basis, dist) = InterpretBasis(basisRaw, method);
        if (!TryD(r["age14CBP"], out var mean))
        {
            n.Diags.Add(Diag.Error("AGE_INCOMPLETE", loc, "age-measurement requires numeric age14CBP (raw age retained on material)"));
            return;
        }
        if (!TryD(r["sigmaYears"], out var sigma))
        {
            n.Diags.Add(Diag.Error("AGE_INCOMPLETE", loc, "age-measurement requires numeric sigmaYears"));
            return;
        }
        long tail = r["tailDraws"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
        double[]? emp = null;
        if (r["samples14CBP"] is JsonArray ja)
        {
            emp = ja.OfType<JsonValue>()
                .Select(v => v.TryGetValue<double>(out var x) ? x : double.NaN)
                .Where(x => !double.IsNaN(x)).ToArray();
            if (emp.Length == 0)
                n.Diags.Add(Diag.Warn("EMPIRICAL_EMPTY", loc, "samples14CBP present but contains no numeric values; distribution kept as parametric"));
        }
        if (dist == DistKind.Longtail14 && tail <= 0)
            n.Diags.Add(Diag.Warn("TAIL_DRAWS_MISSING", loc, "long-tail distribution declared without tailDraws; core parametric draws only"));
        set.Anchors.Add(new Anchor(
            m.Lineage ?? m.Id, r["sample"]?.GetValue<string>() ?? m.Lineage ?? m.Id,
            method, basis, basisRaw,
            depth.Metres, depth, mean, sigma, dist, tail, emp,
            r["reservoir"]?.GetValue<string>(), excluded, reason, m.Id));
    }

    static (string basis, DistKind kind) InterpretBasis(string raw, string method)
    {
        var b = raw.ToLowerInvariant();
        if (b.Contains("reservoir")) return ("14C BP reservoir corrected", DistKind.Normal14);
        if (b.Contains("14c")) return ("14C BP uncorrected", DistKind.Normal14);
        if (b.Contains("cal") || method.ToLowerInvariant().Contains("tephra") || method.ToLowerInvariant().Contains("optically"))
            return ("cal BP", DistKind.NormalCal);
        return ("unspecified", DistKind.Normal14);
    }

    static void ApplyReservoirs(Normalizer n, NormalizedSet set)
    {
        var byLineage = set.Reservoirs.ToDictionary(r => r.Lineage);
        foreach (var a in set.Anchors.ToArray())
        {
            if (a.Basis != "14C BP uncorrected") continue;
            if (a.ReservoirLineage is null)
            {
                n.Diags.Add(Diag.Warn("RESERVOIR_UNRESOLVED", $"anchor:{a.Lineage}",
                    "14C age has no reservoir offset reference; anchor retained but ineligible for 14C model basis"));
                continue;
            }
            if (!byLineage.TryGetValue(a.ReservoirLineage, out var res))
            {
                n.Diags.Add(Diag.Warn("RESERVOIR_UNRESOLVED", $"anchor:{a.Lineage}",
                    $"reservoir '{a.ReservoirLineage}' not found; anchor ineligible for 14C model basis"));
                continue;
            }
            var idx = set.Anchors.IndexOf(a);
            set.Anchors[idx] = a with
            {
                Mean14CBP = a.Mean14CBP - res.DeltaR,
                Sigma14Yr = Math.Sqrt(a.Sigma14Yr * a.Sigma14Yr + res.SigmaYr * res.SigmaYr),
                Basis = "14C BP reservoir corrected",
                EmpiricalSamples14CBP = a.EmpiricalSamples14CBP?.Select(x => x - res.DeltaR).ToArray()
            };
        }
    }

    static void OrderChecks(Normalizer n, NormalizedSet set)
    {
        foreach (var s in set.Segments)
        {
            if (!s.Top.Convertible || !s.Bottom.Convertible) continue;
            if (s.Bottom.Metres <= s.Top.Metres)
                n.Diags.Add(Diag.Error("SEGMENT_ORDER_INVALID", $"segment:{s.Lineage}",
                    $"segment '{s.Name}' bottom {s.Bottom.Metres} m is not below top {s.Top.Metres} m"));
        }
        var depths = new Dictionary<double, List<string>>();
        foreach (var a in set.Anchors.Where(x => !x.Excluded && x.DepthRaw.Convertible))
        {
            var key = Math.Round(a.DepthM * 1000) / 1000.0;
            if (!depths.TryGetValue(key, out var list)) depths[key] = list = new List<string>();
            list.Add(a.Lineage);
        }
        foreach (var (d, lines) in depths.Where(kv => kv.Value.Count > 1))
            n.Diags.Add(Diag.Warn("DEPTH_DUPLICATE", $"depth:{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}m",
                "multiple anchors at same depth: " + string.Join(", ", lines.OrderBy(x => x))));
    }

    static BoundaryKind ParseBoundary(string s) => s.ToLowerInvariant() switch
    {
        "unconformity" => BoundaryKind.Unconformity,
        "fault" => BoundaryKind.Fault,
        "none" => BoundaryKind.None,
        _ => BoundaryKind.Hiatus
    };

    static bool TryD(JsonNode? node, out double v)
    {
        v = 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) { v = d; return true; }
        if (node is JsonValue js && js.TryGetValue<string>(out var s)
            && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d2)) { v = d2; return true; }
        return false;
    }
}
