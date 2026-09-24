using System.Text.Json;

namespace Chrono.Core;

/// <summary>A bundle of the original, domain-specific source documents (not a generic spreadsheet).</summary>
public sealed record SourceBundle(
    string CoreLogJson,
    string AgeTableJson,
    string TephraJson,
    string ReservoirJson,
    string ExclusionsJson);

/// <summary>Three structurally different failure outcomes for import.</summary>
public enum ImportOutcome { Imported, FormatCorrupt, SemanticRejected }

public sealed record ImportResult(
    ImportOutcome Outcome,
    MaterialSet? Materials,
    IReadOnlyList<ImportDiagnostic> Diagnostics,
    string? CorruptLocation);

/// <summary>
/// Parses sedimentology source documents into the immutable material layer. Raw representations
/// (strings, original units) survive on every quantity; normalization happens only here in the
/// derived/import boundary. Unconvertible values stay in place with diagnostics.
/// </summary>
public static class SourceParser
{
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public static ImportResult Parse(SourceBundle bundle, string revisionId)
    {
        var diags = new List<ImportDiagnostic>();
        JsonDocument? core = null, age = null, tephra = null, res = null, excl = null;

        if (!TryOpen(bundle.CoreLogJson, "$.coreLog", out core, out var cLoc))
            return Corrupt(cLoc, "core log");
        if (!TryOpen(bundle.AgeTableJson, "$.ageTable", out age, out var aLoc))
            return Corrupt(aLoc, "age table");
        if (!TryOpen(bundle.TephraJson, "$.tephra", out tephra, out var tLoc))
            return Corrupt(tLoc, "tephra correlation");
        if (!TryOpen(bundle.ReservoirJson, "$.reservoir", out res, out var rLoc))
            return Corrupt(rLoc, "reservoir offsets");
        if (!TryOpen(bundle.ExclusionsJson, "$.exclusions", out excl, out var eLoc))
            return Corrupt(eLoc, "exclusions");

        var root = core!.RootElement;
        var datum = new DepthDatum
        {
            Id = Str(root, "datumId") ?? "datum-unset",
            Name = Str(root, "coreName") ?? "",
            ZeroMbsf = Num(root, "zeroMbsf") ?? 0.0,
            DepthDirection = Str(root, "depthDirection") ?? "down-positive",
            VerticalDatum = Str(root, "verticalDatum") ?? "seafloor"
        };
        if (root.TryGetProperty("datumId", out _) == false)
            diags.Add(new("DATUM_MISSING", "error", "$.coreLog.datumId", "Depth datum id missing; not substituted with a default."));

        var segments = new List<Segment>();
        foreach (var el in root.GetProperty("segments").EnumerateArray())
        {
            var rel = Str(el, "baseRelation") switch
            {
                "core-gap" => SegmentRelation.CoreGap,
                "unconformity" => SegmentRelation.Unconformity,
                _ => SegmentRelation.Continuous
            };
            segments.Add(new Segment
            {
                Id = Req(el, "id", "$.coreLog.segments", diags),
                Lithology = Str(el, "lithology") ?? "",
                TopDepthM = Num(el, "topDepthM") ?? double.NaN,
                BaseDepthM = Num(el, "baseDepthM") ?? double.NaN,
                BaseRelation = rel,
                MissingBaseKa = Num(el, "missingBaseKa"),
                MissingTopKa = Num(el, "missingTopKa"),
                Note = Str(el, "note")
            });
        }

        var offsets = new List<ReservoirOffset>();
        foreach (var el in res!.RootElement.GetProperty("offsets").EnumerateArray())
        {
            var id = Req(el, "id", "$.reservoir.offsets", diags);
            offsets.Add(new ReservoirOffset
            {
                Id = id,
                DeltaR = Qty(el, "deltaR", "deltaRUnit", MeasureKind.Offset, $"$.reservoir.offsets[{id}]", diags),
                SigmaR = Qty(el, "sigmaR", "sigmaRUnit", MeasureKind.Offset, $"$.reservoir.offsets[{id}]", diags),
                AppliesToSampleType = Str(el, "appliesToSampleType") ?? "",
                Note = Str(el, "note") ?? ""
            });
        }

        var anchors = new List<AgeAnchor>();
        foreach (var el in age!.RootElement.GetProperty("ages").EnumerateArray())
        {
            var id = Req(el, "id", "$.ageTable.ages", diags);
            var loc = $"$.ageTable.ages[{id}]";
            var depth = Qty(el, "depthRaw", "depthUnit", MeasureKind.Length, loc, diags);
            var kind = Str(el, "sampleType") switch
            {
                "cal-14c" => AgeSampleType.Calibrated14C,
                "OSL" => AgeSampleType.OSL,
                "tephra" => AgeSampleType.Tephra,
                "210Pb" => AgeSampleType.Pb210,
                "U-Th" => AgeSampleType.UTh,
                _ => AgeSampleType.Unsupported
            };
            if (kind == AgeSampleType.Unsupported)
                diags.Add(new("SAMPLE_TYPE_UNKNOWN", "error", loc + ".sampleType",
                    $"Anchor {id} has unsupported sample type '{Str(el, "sampleType")}'; kept raw, not modelled.", new[] { id }));

            var dist = ParseDistribution(el, id, diags);
            anchors.Add(new AgeAnchor
            {
                Id = id,
                Label = Str(el, "label") ?? id,
                Depth = depth,
                Distribution = dist,
                ReservoirOffsetId = Str(el, "reservoirOffsetId"),
                SourceRef = Str(el, "sourceRef") ?? ""
            });
        }

        var events = new List<EventCorrelation>();
        foreach (var el in tephra!.RootElement.GetProperty("events").EnumerateArray())
        {
            var id = Req(el, "id", "$.tephra.events", diags);
            var loc = $"$.tephra.events[{id}]";
            events.Add(new EventCorrelation
            {
                Id = id,
                Name = Str(el, "name") ?? id,
                Depth = Qty(el, "depthRaw", "depthUnit", MeasureKind.Length, loc, diags),
                AgeCalBP = el.TryGetProperty("ageRaw", out _)
                    ? Qty(el, "ageRaw", "ageUnit", MeasureKind.Age, loc, diags) : null,
                CorrelationBasis = Str(el, "correlationBasis") ?? "",
                ExternalRef = Str(el, "externalRef") ?? ""
            });
        }

        var exclusions = new List<ExclusionDecision>();
        foreach (var el in excl!.RootElement.GetProperty("exclusions").EnumerateArray())
        {
            var target = Req(el, "targetMaterialId", "$.exclusions.exclusions", diags);
            exclusions.Add(new ExclusionDecision
            {
                TargetMaterialId = target,
                Reason = Str(el, "reason") ?? "",
                DecidedByEvent = Str(el, "decidedByEvent") ?? revisionId
            });
        }

        // apply exclusions to anchors (decisions remain visible in both places)
        foreach (var x in exclusions)
        {
            var idx = anchors.FindIndex(a => a.Id == x.TargetMaterialId);
            if (idx >= 0)
            {
                anchors[idx] = anchors[idx] with
                {
                    Excluded = !x.LaterRevoked,
                    ExcludeReason = x.Reason,
                    EventId = x.DecidedByEvent
                };
            }
            else
                diags.Add(new("EXCLUSION_TARGET_MISSING", "warning",
                    $"$.exclusions.exclusions[{x.TargetMaterialId}]",
                    $"Exclusion references unknown material {x.TargetMaterialId}; retained as a dangling decision.",
                    new[] { x.TargetMaterialId }));
        }

        // semantic layout checks (distinct from parse failure): overlapping segments
        var ordered = segments.OrderBy(s => s.TopDepthM).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].TopDepthM < ordered[i - 1].BaseDepthM - 1e-9)
                diags.Add(new("SEGMENT_OVERLAP", "error",
                    $"$.coreLog.segments[{ordered[i].Id}]",
                    $"Segment {ordered[i].Id} overlaps {ordered[i - 1].Id}; layout semantically rejected.",
                    new[] { ordered[i].Id, ordered[i - 1].Id }));

        var set = new MaterialSet
        {
            RevisionId = revisionId,
            Datum = datum,
            Segments = segments,
            Anchors = anchors,
            ReservoirOffsets = offsets,
            Events = events,
            Diagnostics = diags,
            Exclusions = exclusions
        };
        var outcome = diags.Any(d => d.Severity == "error" &&
            (d.Code is "SEGMENT_OVERLAP" or "DATUM_MISSING"))
            ? ImportOutcome.SemanticRejected : ImportOutcome.Imported;
        return new ImportResult(outcome, outcome == ImportOutcome.SemanticRejected ? null : set, diags, null);
    }

    private static AgeDistribution ParseDistribution(JsonElement el, string id, List<ImportDiagnostic> diags)
    {
        var loc = $"$.ageTable.ages[{id}].distribution";
        if (el.TryGetProperty("distribution", out var d) == false)
        {
            diags.Add(new("DISTRIBUTION_MISSING", "error", loc,
                $"Anchor {id} has no distribution; a mean cannot stand in for it.", new[] { id }));
            return new AgeDistribution { Id = id };
        }
        var kind = d.TryGetProperty("kind", out var k) ? k.GetString() ?? "empirical" : "empirical";
        var samples = Array.Empty<double>();
        if (d.TryGetProperty("samplesCalBP", out var sa))
            samples = sa.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        double? mu = d.TryGetProperty("muCalBP", out var muE) ? muE.GetDouble() : null;
        double? sg = d.TryGetProperty("sigmaCalBP", out var sgE) ? sgE.GetDouble() : null;
        double? skew = d.TryGetProperty("skew", out var skE) ? skE.GetDouble() : null;
        int? tail = d.TryGetProperty("longTailPpm", out var lt) ? lt.GetInt32() : null;
        string? note = d.TryGetProperty("tailNote", out var tn) ? tn.GetString() : null;
        if (samples.Length is > 0 and < 8)
            diags.Add(new("SAMPLE_TOO_SPARSE", "warning", loc + ".samplesCalBP",
                $"Anchor {id} empirical distribution has only {samples.Length} samples.", new[] { id }));
        return new AgeDistribution
        {
            Id = id,
            DistributionKind = kind,
            SamplesCalBP = samples,
            MuCalBP = mu,
            SigmaCalBP = sg,
            Skew = skew,
            LongTailPpm = tail,
            TailNote = note
        };
    }

    private static RawQuantity Qty(JsonElement el, string rawProp, string unitProp, MeasureKind kind,
        string loc, List<ImportDiagnostic> diags)
    {
        var raw = el.TryGetProperty(rawProp, out var r) ? r.ToString() : "";
        var unit = el.TryGetProperty(unitProp, out var u) ? u.GetString() : null;
        var q = RawQuantity.Parse(raw, kind, unit);
        if (q.UnconvertedReason is not null)
            diags.Add(new("UNCONVERTED_VALUE",
                kind == MeasureKind.Age && unit == "BP" ? "warning" : "error",
                $"{loc}.{rawProp}", $"{loc}: value '{raw}' left in place ({q.UnconvertedReason}); not dropped for plotting.",
                Array.Empty<string>()));
        return q;
    }

    private static bool TryOpen(string json, string loc, out JsonDocument doc, out string? errorLoc)
    {
        errorLoc = null;
        try { doc = JsonDocument.Parse(json, DocOpts); return true; }
        catch (JsonException ex)
        {
            doc = null!;
            errorLoc = $"{loc} <byte {ex.LineNumber}: {ex.Message.Split('\n')[0]}>";
            return false;
        }
    }

    private static ImportResult Corrupt(string? loc, string what) =>
        new(ImportOutcome.FormatCorrupt, null,
            new[] { new ImportDiagnostic("error", "FORMAT_CORRUPT", loc ?? "$",
                $"The {what} document is malformed; import aborts at the located token instead of guessing.",
                Array.Empty<string>()) }, loc);

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static string Req(JsonElement el, string name, string loc, List<ImportDiagnostic> diags)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
        diags.Add(new("FIELD_MISSING", "error", $"{loc}.{name}", $"Required field '{name}' missing.", Array.Empty<string>()));
        return $"{name}-missing";
    }
}
