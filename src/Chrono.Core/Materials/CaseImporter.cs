using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Model;
using Chrono.Core.Quantities;

namespace Chrono.Core.Materials;

/// <summary>
/// Offline case-package parser. Raw representations are always preserved; normalization happens
/// in a derived layer. Values that cannot be converted stay in place with a diagnostic — they are
/// never dropped to make plotting possible.
/// </summary>
public sealed class CaseImporter
{
    public sealed record ImportOutcome(
        bool Ok,
        DerivationFailure Failure,
        string? FormatError,
        List<Material> Materials,
        List<Diagnostic> Diagnostics);

    private sealed record DistParse(
        bool Ok,
        IAgeDistribution? Value,
        string RawRepresentation,
        string Kind,
        Diagnostic? Diagnostic);

    public ImportOutcome ParseDirectory(string caseDir)
    {
        if (!Directory.Exists(caseDir))
            return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                $"case directory not found: {caseDir}", new List<Material>(), new List<Diagnostic>());

        var materials = new List<Material>();
        var diagnostics = new List<Diagnostic>();
        var files = new[]
        {
            "depth-datum.json", "segments.json", "gaps.json", "radiometric-dates.json",
            "reservoir-offsets.json", "event-ties.json", "exclusions.json"
        };

        foreach (var file in files)
        {
            var path = Path.Combine(caseDir, file);
            if (!File.Exists(path))
            {
                diagnostics.Add(new Diagnostic("MISSING_CASE_FILE", $"required case file absent: {file}", file));
                continue;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                    $"{file}: unreadable ({ex.GetType().Name})", materials, diagnostics);
            }
            var outcome = ParseFile(file, text);
            if (outcome.Failure != DerivationFailure.None)
                return new ImportOutcome(false, outcome.Failure, outcome.FormatError, materials, diagnostics);
            materials.AddRange(outcome.Materials);
            diagnostics.AddRange(outcome.Diagnostics);
        }
        return new ImportOutcome(true, DerivationFailure.None, null, materials, diagnostics);
    }

    public ImportOutcome ParseFile(string file, string text)
    {
        var materials = new List<Material>();
        var diagnostics = new List<Diagnostic>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                $"{file}: malformed JSON ({ex.Message})", materials, diagnostics);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                    $"{file}: top-level must be an array", materials, diagnostics);

            int line = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                line++;
                string path = $"{file}#L{line}";
                try
                {
                    var (material, parseDiag) = ParseRow(file, line, path, row);
                    if (parseDiag is not null)
                        return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                            $"{path}: {parseDiag}", materials, diagnostics);
                    if (material is not null)
                    {
                        materials.Add(material);
                        diagnostics.AddRange(material.Diagnostics);
                    }
                }
                catch (KeyNotFoundException ex)
                {
                    return new ImportOutcome(false, DerivationFailure.FormatCorrupt,
                        $"{path}: missing field {ex.Message}", materials, diagnostics);
                }
            }
        }
        return new ImportOutcome(true, DerivationFailure.None, null, materials, diagnostics);
    }

    private static (Material?, string?) ParseRow(string file, int line, string path, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return (null, "row is not an object");
        string id = Req(row, "id").GetString()!;
        string core = row.TryGetProperty("coreId", out var c) ? c.GetString() ?? "" : "";

        switch (file)
        {
            case "depth-datum.json":
            {
                var datum = Req(row, "datum").GetString()!;
                var dir = Req(row, "depthDirection").GetString()!;
                if (!Enum.TryParse<DepthDirection>(dir, true, out var direction))
                    return (null, $"unknown depthDirection '{dir}'");
                return (new DepthDatumMaterial(id, path, file, line, core, datum, direction), null);
            }
            case "segments.json":
            {
                var name = Req(row, "name").GetString()!;
                var datum = Req(row, "depthDatum").GetString()!;
                var dir = ParseDirection(row);
                var unit = Req(row, "depthUnit").GetString()!;
                var structure = Enum.TryParse<SegmentStructure>(Req(row, "structure").GetString(), true, out var st)
                    ? st : SegmentStructure.Continuous;
                var top = DepthUnits.Parse(row.TryGetProperty("topDepth", out var t) ? t.GetString() : null,
                    unit, datum, dir, $"{path}.topDepth");
                var bottom = DepthUnits.Parse(row.TryGetProperty("bottomDepth", out var b) ? b.GetString() : null,
                    unit, datum, dir, $"{path}.bottomDepth");
                var seg = new CoreSegmentMaterial(id, path, file, line, core, name,
                    top.RawRepresentation, bottom.RawRepresentation, unit, datum, dir, structure,
                    top.Ok ? top.Value : null, bottom.Ok ? bottom.Value : null);
                AddDiag(seg, top); AddDiag(seg, bottom);
                return (seg, null);
            }
            case "gaps.json":
            {
                var kind = Enum.Parse<GapKind>(Req(row, "gapKind").GetString()!, true);
                var datum = Req(row, "depthDatum").GetString()!;
                var dir = ParseDirection(row);
                var unit = Req(row, "depthUnit").GetString()!;
                var note = row.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
                var at = DepthUnits.Parse(row.TryGetProperty("atDepth", out var a) ? a.GetString() : null,
                    unit, datum, dir, $"{path}.atDepth");
                var gap = new GapMaterial(id, path, file, line, core, kind, at.RawRepresentation,
                    unit, datum, dir, note, at.Ok ? at.Value : null);
                AddDiag(gap, at);
                return (gap, null);
            }
            case "radiometric-dates.json":
            {
                var lab = Req(row, "labCode").GetString()!;
                var datum = Req(row, "depthDatum").GetString()!;
                var dir = ParseDirection(row);
                var unit = Req(row, "depthUnit").GetString()!;
                var at = DepthUnits.Parse(row.TryGetProperty("atDepth", out var a) ? a.GetString() : null,
                    unit, datum, dir, $"{path}.atDepth");
                var age = ParseDistribution(row.GetProperty("measuredAge"), $"{path}.measuredAge");
                string? resId = row.TryGetProperty("reservoirOffsetId", out var r) ? r.GetString() : null;
                var d = new RadiometricDateMaterial(id, path, file, line, core, lab,
                    at.RawRepresentation, unit, datum, dir, age.Ok ? age.Value : null, resId,
                    at.Ok ? at.Value : null)
                {
                    Excluded = row.TryGetProperty("excluded", out var ex) && ex.GetBoolean(),
                    ExclusionReason = row.TryGetProperty("exclusionReason", out var er) ? er.GetString() : null
                };
                AddDiag(d, at);
                if (age.Diagnostic is not null) d.Diagnostics.Add(age.Diagnostic);
                return (d, null);
            }
            case "reservoir-offsets.json":
            {
                var name = Req(row, "name").GetString()!;
                var dist = ParseDistribution(row.GetProperty("offset"), $"{path}.offset");
                bool enabled = !row.TryGetProperty("enabled", out var en) || en.GetBoolean();
                var m = new ReservoirMaterial(id, path, file, line, core, name,
                    dist.Ok ? dist.Value! : new NormalAgeDistribution(0, 0, "BP1950"), enabled);
                if (dist.Diagnostic is not null) m.Diagnostics.Add(dist.Diagnostic);
                return (m, null);
            }
            case "event-ties.json":
            {
                var ev = Req(row, "eventCode").GetString()!;
                var datum = Req(row, "depthDatum").GetString()!;
                var dir = ParseDirection(row);
                var unit = Req(row, "depthUnit").GetString()!;
                var at = DepthUnits.Parse(row.TryGetProperty("atDepth", out var a) ? a.GetString() : null,
                    unit, datum, dir, $"{path}.atDepth");
                var ageEl = row.TryGetProperty("correlatedAge", out var ca) ? ca : default;
                var age = ageEl.ValueKind == JsonValueKind.Object
                    ? ParseDistribution(ageEl, $"{path}.correlatedAge")
                    : new DistParse(false, null, "", "",
                        new Diagnostic("MISSING_DISTRIBUTION", "event tie has no correlatedAge", path));
                var basis = row.TryGetProperty("correlationBasis", out var bs) ? bs.GetString() ?? "" : "";
                var e = new EventTieMaterial(id, path, file, line, core, ev,
                    at.RawRepresentation, unit, datum, dir, age.Ok ? age.Value : null, basis,
                    at.Ok ? at.Value : null)
                {
                    Excluded = row.TryGetProperty("excluded", out var ex2) && ex2.GetBoolean(),
                    ExclusionReason = row.TryGetProperty("exclusionReason", out var er2) ? er2.GetString() : null,
                    CorrelationGroup = row.TryGetProperty("correlationGroup", out var cg) ? cg.GetString() : null
                };
                AddDiag(e, at);
                if (age.Diagnostic is not null) e.Diagnostics.Add(age.Diagnostic);
                return (e, null);
            }
            case "exclusions.json":
            {
                var target = Req(row, "targetMaterialId").GetString()!;
                var reason = Req(row, "reason").GetString()!;
                var by = row.TryGetProperty("decidedBy", out var w) ? w.GetString() ?? "" : "";
                return (new ExclusionDecisionMaterial(id, path, file, line, target, reason, by), null);
            }
            default:
                return (null, $"unknown case file {file}");
        }
    }

    private static DepthDirection ParseDirection(JsonElement row)
    {
        if (row.TryGetProperty("depthDirection", out var d))
            return Enum.Parse<DepthDirection>(d.GetString()!, true);
        return DepthDirection.PositiveDown;
    }

    private static void AddDiag(Material m, Normalization<Depth> n)
    {
        if (n.Diagnostic is not null) m.Diagnostics.Add(n.Diagnostic);
    }

    private static DistParse ParseDistribution(JsonElement el, string path)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return new(false, null, el.ToString(), "",
                new Diagnostic("DISTRIBUTION_NOT_OBJECT", "distribution must be an object", path));
        string kind = el.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        string datum = el.TryGetProperty("datum", out var dd) ? dd.GetString() ?? "BP1950" : "BP1950";
        string raw = el.GetRawText();
        try
        {
            IAgeDistribution dist = kind switch
            {
                "normal" => new NormalAgeDistribution(
                    Req(el, "mean").GetDouble(), Req(el, "sigma").GetDouble(), datum),
                "student_t" => new StudentTAgeDistribution(
                    Req(el, "location").GetDouble(), Req(el, "scale").GetDouble(),
                    Req(el, "df").GetDouble(), datum),
                "mixture" => ParseMixture(el, datum, path),
                _ => throw new FormatException($"unknown distribution kind '{kind}'")
            };
            return new DistParse(true, dist, raw, kind, null);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException)
        {
            return new(false, null, raw, kind,
                new Diagnostic("DISTRIBUTION_UNPARSEABLE", ex.Message, path));
        }
    }

    private static IAgeDistribution ParseMixture(JsonElement el, string datum, string path)
    {
        var comps = new List<MixtureComponent>();
        foreach (var c in Req(el, "components").EnumerateArray())
        {
            double weight = Req(c, "weight").GetDouble();
            var inner = ParseDistribution(c.GetProperty("distribution"), path + ".components");
            if (!inner.Ok)
                throw new FormatException(inner.Diagnostic!.Message);
            comps.Add(new MixtureComponent(weight, inner.Value!));
        }
        if (comps.Count == 0) throw new FormatException("mixture has no components");
        double sum = comps.Sum(c => c.Weight);
        if (Math.Abs(sum - 1.0) > 1e-6)
            // weights are not silently renormalized: a semantic issue the analyst must see
            throw new FormatException($"mixture weights sum to {sum}, expected 1.0");
        return new MixtureAgeDistribution(comps, datum);
    }

    private static JsonElement Req(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v
            : throw new KeyNotFoundException(name);
}
