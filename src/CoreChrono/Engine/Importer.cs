using System.Text.Json;
using CoreChrono.Domain;

namespace CoreChrono.Engine;

/// <summary>Three distinct failure surfaces, per specification:
/// 1. FormatCorrupt  - the document is not parseable / structurally wrong (hard failure)
/// 2. Incomplete     - parseable but missing/inconsistent, with positioned diagnostics
/// 3. Semantic       - well-formed, complete, but no unique depth-age solution exists
/// Resource exhaustion is reported separately by the engine (BudgetExhausted).</summary>
public enum ImportFailureKind { None, FormatCorrupt, Incomplete, Semantic }

public sealed class ImportReport
{
    public required ImportFailureKind Kind { get; init; }
    public EvidenceBundle? Bundle { get; init; }
    public List<ImportDiagnostic> Diagnostics { get; init; } = new();
    public string? CorruptDetail { get; init; }
    public bool Succeeded => Kind == ImportFailureKind.None && Bundle is not null;
}

public static class Importer
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static ImportReport Import(string jsonText)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException ex)
        {
            return new ImportReport
            {
                Kind = ImportFailureKind.FormatCorrupt,
                CorruptDetail = $"malformed JSON at {ex.BytePositionInLine}: {ex.Message}",
            };
        }
        using var docDisposable = doc;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new ImportReport { Kind = ImportFailureKind.FormatCorrupt, CorruptDetail = "root is not an object" };
        if (!root.TryGetProperty("schemaVersion", out _))
            return new ImportReport
            {
                Kind = ImportFailureKind.FormatCorrupt,
                Diagnostics = new() { new ImportDiagnostic("SCHEMA_MARKER_MISSING", "$.schemaVersion",
                    "document is not a CoreChrono evidence bundle", "error") },
            };

        var diag = new List<ImportDiagnostic>();
        var fragments = new List<string>();
        var bundle = new EvidenceBundle { SchemaVersion = root.GetProperty("schemaVersion").GetString() ?? "?" };

        bundle.Datum = ParseDatum(root, diag);
        bundle.Segments = ParseSegments(root, diag, fragments);
        bundle.Anchors = ParseAnchors(root, diag, fragments);
        bundle.ReservoirOffsets = ParseOffsets(root, diag, fragments);
        bundle.EventBeds = ParseEventBeds(root, diag, fragments);
        bundle.Exclusions = ParseExclusions(root, diag);
        bundle.Parameters = ParseParameters(root);
        if (root.TryGetProperty("inputFragments", out var fr) && fr.ValueKind == JsonValueKind.Array)
            fragments.AddRange(fr.EnumerateArray().Select(x => x.GetString() ?? ""));
        bundle.InputFragments = fragments.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        bundle.Diagnostics = diag;

        if (diag.Any(d => d.Severity == "error"))
            return new ImportReport { Kind = ImportFailureKind.Incomplete, Bundle = bundle, Diagnostics = diag };

        return new ImportReport { Kind = ImportFailureKind.None, Bundle = bundle, Diagnostics = diag };
    }

    // ------------------------------------------------------------------
    private static DepthDatum? ParseDatum(JsonElement root, List<ImportDiagnostic> diag)
    {
        if (!root.TryGetProperty("datum", out var d))
        {
            diag.Add(new ImportDiagnostic("DATUM_MISSING", "$.datum", "no depth datum declared", "error"));
            return null;
        }
        return new DepthDatum
        {
            Id = d.TryGetProperty("id", out var idv) ? idv.GetString() ?? "D1" : "D1",
            Name = d.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "",
            VerticalDirection = d.TryGetProperty("verticalDirection", out var vv) ? vv.GetString() ?? "down-positive" : "down-positive",
            NormalizedUnit = d.TryGetProperty("normalizedUnit", out var nu) ? nu.GetString() ?? "m" : "m",
            ReferenceOffsetM = d.TryGetProperty("referenceOffsetM", out var ro) && ro.TryGetDouble(out var rod) ? rod : null,
        };
    }

    private static Measure ParseMeasure(JsonElement el, bool depth, string path, List<ImportDiagnostic> diag,
        string? datum = null, bool required = true)
    {
        string raw = "", unit = depth ? "m" : "ka BP";
        if (el.ValueKind == JsonValueKind.Object)
        {
            raw = el.TryGetProperty("raw", out var rv) ? rv.GetString() ?? "" : "";
            unit = el.TryGetProperty("unit", out var uv) ? uv.GetString() ?? unit : unit;
            datum ??= el.TryGetProperty("datum", out var dv) ? dv.GetString() : null;
        }
        else if (el.ValueKind == JsonValueKind.Number)
        {
            raw = el.GetRawText();
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            raw = el.GetString() ?? "";
        }

        if (required && string.IsNullOrWhiteSpace(raw))
            diag.Add(new ImportDiagnostic(depth ? "DEPTH_MISSING" : "AGE_MISSING", path,
                "measure has no raw value", "error"));

        var normalized = depth
            ? UnitTables.TryConvertLength(raw, unit, out var d1)
            : UnitTables.TryConvertTime(raw, unit, out d1);
        if (normalized.HasValue)
            return new Measure
            {
                Raw = raw, Unit = unit, Datum = datum,
                Normalized = normalized.Value,
                NormalizedUnit = depth ? "m" : "ka",
            };
        if (string.IsNullOrWhiteSpace(raw))
            return Measure.Unconvertible("", unit, d1 ?? "missing");
        // unconvertible value stays in place with a diagnostic; never dropped for plotting
        diag.Add(new ImportDiagnostic("UNCONVERTIBLE", path, d1 ?? "unconvertible", "error"));
        return Measure.Unconvertible(raw, unit, d1 ?? "unconvertible") with { Datum = datum };
    }

    private static List<Segment> ParseSegments(JsonElement root, List<ImportDiagnostic> diag, List<string> frag)
    {
        var result = new List<Segment>();
        if (!root.TryGetProperty("segments", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            diag.Add(new ImportDiagnostic("SEGMENTS_MISSING", "$.segments", "no core segments", "error"));
            return result;
        }
        var idx = 0;
        foreach (var s in arr.EnumerateArray())
        {
            var path = $"$.segments[{idx}]";
            var id = s.TryGetProperty("id", out var iv) ? iv.GetString() ?? $"S{idx + 1}" : $"S{idx + 1}";
            var label = s.TryGetProperty("label", out var lv) ? lv.GetString() ?? id : id;
            var top = s.TryGetProperty("topDepth", out var tv) ? ParseMeasure(tv, true, path + ".topDepth", diag)
                : Measure.Unconvertible("", "m", "missing topDepth");
            var bot = s.TryGetProperty("bottomDepth", out var bv) ? ParseMeasure(bv, true, path + ".bottomDepth", diag)
                : Measure.Unconvertible("", "m", "missing bottomDepth");
            var upper = ParseBoundary(s, "upperBoundary");
            var lower = ParseBoundary(s, "lowerBoundary");
            var ordering = s.TryGetProperty("ordering", out var ov) && ov.ValueKind == JsonValueKind.Number
                ? ov.GetInt32() : 1;
            var note = s.TryGetProperty("boundaryNote", out var nv) ? nv.GetString() : null;
            if (s.TryGetProperty("sourceRef", out var rv) && rv.GetString() is { } src) frag.Add(src);
            result.Add(new Segment
            {
                Id = id, Label = label, TopDepth = top, BottomDepth = bot,
                UpperBoundary = upper, LowerBoundary = lower, BoundaryNote = note, Ordering = ordering,
            });
            idx++;
        }
        return result;
    }

    private static BoundaryKind ParseBoundary(JsonElement s, string prop)
    {
        if (!s.TryGetProperty(prop, out var v)) return BoundaryKind.Conformable;
        var txt = v.GetString() ?? "conformable";
        return txt.ToLowerInvariant() switch
        {
            "gap" => BoundaryKind.Gap,
            "unconformity" => BoundaryKind.Unconformity,
            "fault" => BoundaryKind.Fault,
            _ => BoundaryKind.Conformable,
        };
    }

    private static AgeDistribution ParseDistribution(JsonElement el, string path, List<ImportDiagnostic> diag)
    {
        var kind = el.TryGetProperty("kind", out var kv) ? kv.GetString() ?? "normal" : "normal";
        double Num(string n) => el.TryGetProperty(n, out var v) && v.TryGetDouble(out var d) ? d : double.NaN;
        switch (kind.ToLowerInvariant())
        {
            case "normal":
                if (double.IsNaN(Num("meanKa")) || double.IsNaN(Num("sigmaKa")))
                    diag.Add(new ImportDiagnostic("DISTRIBUTION_BAD", path, "normal needs meanKa and sigmaKa", "error"));
                return new NormalAge(Num("meanKa"), Num("sigmaKa"));
            case "lognormal":
                if (double.IsNaN(Num("medianKa")) || double.IsNaN(Num("sigmaLog")))
                    diag.Add(new ImportDiagnostic("DISTRIBUTION_BAD", path, "lognormal needs medianKa and sigmaLog", "error"));
                return new LognormalAge(Num("medianKa"), Num("sigmaLog"));
            case "studentt":
                if (double.IsNaN(Num("locationKa")) || double.IsNaN(Num("scaleKa")))
                    diag.Add(new ImportDiagnostic("DISTRIBUTION_BAD", path, "studentT needs locationKa, scaleKa", "error"));
                return new StudentTAge(Num("locationKa"), Num("scaleKa"), Num("df") is var df && !double.IsNaN(df) ? df : 10);
            case "empirical":
                var samples = el.TryGetProperty("samplesKa", out var sv) && sv.ValueKind == JsonValueKind.Array
                    ? sv.EnumerateArray().Select(x => x.TryGetDouble(out var dd) ? dd : double.NaN).ToList()
                    : new List<double>();
                if (samples.Count == 0)
                    diag.Add(new ImportDiagnostic("DISTRIBUTION_BAD", path, "empirical needs samplesKa", "error"));
                return new EmpiricalAge(samples);
            default:
                diag.Add(new ImportDiagnostic("DISTRIBUTION_UNKNOWN", path, $"unknown distribution kind '{kind}'", "error"));
                return new NormalAge(double.NaN, double.NaN);
        }
    }

    private static List<AgeAnchor> ParseAnchors(JsonElement root, List<ImportDiagnostic> diag, List<string> frag)
    {
        var result = new List<AgeAnchor>();
        if (!root.TryGetProperty("anchors", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        var idx = 0;
        foreach (var a in arr.EnumerateArray())
        {
            var path = $"$.anchors[{idx}]";
            var id = a.TryGetProperty("id", out var iv) ? iv.GetString() ?? $"A{idx + 1}" : $"A{idx + 1}";
            var label = a.TryGetProperty("label", out var lv) ? lv.GetString() ?? id : id;
            var depth = a.TryGetProperty("depth", out var dv)
                ? ParseMeasure(dv, true, path + ".depth", diag)
                : Measure.Unconvertible("", "m", "anchor depth missing");
            if (depth.Datum is null && depth.IsConvertible) depth = depth with { Datum = "mbsf" };
            AgeDistribution dist;
            if (a.TryGetProperty("distribution", out var dtv))
                dist = ParseDistribution(dtv, path + ".distribution", diag);
            else if (a.TryGetProperty("ageKa", out var av))
            {
                // a bare mean is accepted into a distribution slot only if uncertainty provided
                var sigma = a.TryGetProperty("sigmaKa", out var sv) && sv.TryGetDouble(out var sd) ? sd : double.NaN;
                if (double.IsNaN(sigma))
                    diag.Add(new ImportDiagnostic("MEAN_NOT_DISTRIBUTION", path + ".ageKa",
                        "anchor supplied a mean age without a distribution/uncertainty; a distribution must not be replaced by a mean",
                        "error"));
                dist = new NormalAge(av.GetDouble(), double.IsNaN(sigma) ? 0 : sigma);
            }
            else
            {
                diag.Add(new ImportDiagnostic("DISTRIBUTION_MISSING", path, "anchor has no age distribution", "error"));
                dist = new NormalAge(double.NaN, double.NaN);
            }
            if (a.TryGetProperty("sourceRef", out var rv) && rv.GetString() is { } src) frag.Add(src);
            result.Add(new AgeAnchor
            {
                Id = id,
                Label = label,
                Depth = depth,
                SegmentId = a.TryGetProperty("segmentId", out var seg) ? seg.GetString() : null,
                Distribution = dist,
                Material = a.TryGetProperty("material", out var mv) ? mv.GetString() ?? "" : "",
                DatingMethod = a.TryGetProperty("datingMethod", out var dm) ? dm.GetString() ?? "" : "",
                ReservoirOffsetId = a.TryGetProperty("reservoirOffsetId", out var ro) ? ro.GetString() : null,
                TieCandidate = a.TryGetProperty("tieCandidate", out var tc) && tc.GetBoolean(),
                EventBedId = a.TryGetProperty("eventBedId", out var eb) ? eb.GetString() : null,
                SourceRef = a.TryGetProperty("sourceRef", out var sr) ? sr.GetString() ?? "" : "",
            });
            idx++;
        }
        return result;
    }

    private static List<ReservoirOffset> ParseOffsets(JsonElement root, List<ImportDiagnostic> diag, List<string> frag)
    {
        var result = new List<ReservoirOffset>();
        if (!root.TryGetProperty("reservoirOffsets", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        var idx = 0;
        foreach (var o in arr.EnumerateArray())
        {
            var path = $"$.reservoirOffsets[{idx}]";
            var off = o.TryGetProperty("offset", out var ov) ? ParseMeasure(ov, false, path + ".offset", diag)
                : Measure.Unconvertible("", "ka", "offset missing");
            if (off.Datum is null && off.IsConvertible) off = off with { Datum = "BP1950" };
            result.Add(new ReservoirOffset
            {
                Id = o.TryGetProperty("id", out var iv) ? iv.GetString() ?? $"R{idx + 1}" : $"R{idx + 1}",
                AnchorId = o.TryGetProperty("anchorId", out var av) ? av.GetString() ?? "" : "",
                Offset = off,
                Method = o.TryGetProperty("method", out var mv) ? mv.GetString() ?? "" : "",
                TimeBasis = o.TryGetProperty("timeBasis", out var tv) ? tv.GetString() ?? "BP1950" : "BP1950",
            });
            idx++;
        }
        return result;
    }

    private static List<EventBed> ParseEventBeds(JsonElement root, List<ImportDiagnostic> diag, List<string> frag)
    {
        var result = new List<EventBed>();
        if (!root.TryGetProperty("eventBeds", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        var idx = 0;
        foreach (var e in arr.EnumerateArray())
        {
            var path = $"$.eventBeds[{idx}]";
            Measure? depth = null;
            if (e.TryGetProperty("depth", out var dv))
            {
                depth = ParseMeasure(dv, true, path + ".depth", diag, required: false);
                if (!depth.IsConvertible) depth = null;
            }
            AgeDistribution? datumAge = null;
            if (e.TryGetProperty("datumAge", out var av))
                datumAge = ParseDistribution(av, path + ".datumAge", diag);
            result.Add(new EventBed
            {
                Id = e.TryGetProperty("id", out var iv) ? iv.GetString() ?? $"E{idx + 1}" : $"E{idx + 1}",
                Label = e.TryGetProperty("label", out var lv) ? lv.GetString() ?? "" : "",
                Depth = depth,
                DatumAge = datumAge,
                Correlation = e.TryGetProperty("correlation", out var cv) ? cv.GetString() ?? "" : "",
                SourceRef = e.TryGetProperty("sourceRef", out var sr) ? sr.GetString() ?? "" : "",
            });
            if (e.TryGetProperty("sourceRef", out var rv) && rv.GetString() is { } src) frag.Add(src);
            idx++;
        }
        return result;
    }

    private static List<ExclusionDecision> ParseExclusions(JsonElement root, List<ImportDiagnostic> diag)
    {
        var result = new List<ExclusionDecision>();
        if (!root.TryGetProperty("exclusions", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        var idx = 0;
        foreach (var x in arr.EnumerateArray())
        {
            var target = (x.TryGetProperty("targetKind", out var tk) ? tk.GetString() ?? "anchor" : "anchor").ToLowerInvariant() switch
            {
                "eventbed" => ExclusionTarget.EventBed,
                "reservoiroffset" => ExclusionTarget.ReservoirOffset,
                "segment" => ExclusionTarget.Segment,
                _ => ExclusionTarget.Anchor,
            };
            var status = (x.TryGetProperty("status", out var st) ? st.GetString() ?? "accepted" : "accepted").ToLowerInvariant() switch
            {
                "proposed" => DecisionStatus.Proposed,
                "rejected" => DecisionStatus.Rejected,
                "rescinded" => DecisionStatus.Rescinded,
                "superseded" => DecisionStatus.Superseded,
                _ => DecisionStatus.Accepted,
            };
            result.Add(new ExclusionDecision
            {
                Id = x.TryGetProperty("id", out var iv) ? iv.GetString() ?? $"X{idx + 1}" : $"X{idx + 1}",
                TargetKind = target,
                TargetId = x.TryGetProperty("targetId", out var tv) ? tv.GetString() ?? "" : "",
                Reason = x.TryGetProperty("reason", out var rv) ? rv.GetString() ?? "" : "",
                SourceRef = x.TryGetProperty("sourceRef", out var sr) ? sr.GetString() ?? "" : "",
                Status = status,
            });
            idx++;
        }
        return result;
    }

    private static ParameterSet ParseParameters(JsonElement root)
    {
        var ps = new ParameterSet { Version = "P1" };
        if (!root.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object) return ps;
        int? Int(string n) => p.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : null;
        ulong? U64(string n) => p.TryGetProperty(n, out var v) && v.TryGetUInt64(out var i) ? i : null;
        double? Dbl(string n) => p.TryGetProperty(n, out var v) && v.TryGetDouble(out var i) ? i : null;
        return ps with
        {
            Version = p.TryGetProperty("version", out var vv) ? vv.GetString() ?? "P1" : "P1",
            Interpolation = p.TryGetProperty("interpolation", out var iv) ? iv.GetString() ?? "linear" : "linear",
            EnforceWithinSegmentMonotonicity = p.TryGetProperty("enforceWithinSegmentMonotonicity", out var ev) ? ev.GetBoolean() : true,
            MonteCarloDraws = Int("monteCarloDraws") ?? 2000,
            RandomSeed = U64("randomSeed") ?? 20260923UL,
            CandidateBudget = Int("candidateBudget") ?? 256,
            ExtrapolateSegmentM = Dbl("extrapolateSegmentM") ?? 0,
            Note = p.TryGetProperty("note", out var nv) ? nv.GetString() : null,
        };
    }
}
