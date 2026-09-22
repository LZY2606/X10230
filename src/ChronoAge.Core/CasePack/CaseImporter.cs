using System.Globalization;

namespace ChronoAge.Core.CasePack;

public sealed record ImportedBatch(
    string BatchId,
    IReadOnlyList<Material> Materials,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>
/// Maps raw case-pack rows to domain materials. Original tokens survive inside
/// <see cref="Measure.Raw"/>; unconvertible values stay in place with diagnostics.
/// Ids are deterministic from (logId, version) so an export/import round trip keeps identity.
/// </summary>
public static class CaseImporter
{
    public static string MaterialId(string logId, int version) => $"{logId}/v{version}";

    public static ImportedBatch ImportBatch(
        string batchId, IEnumerable<CaseMaterial> rows, int version = 1,
        IReadOnlyDictionary<string, int>? versionOverrides = null)
    {
        var materials = new List<Material>();
        var diags = new List<ImportDiagnostic>();
        foreach (var row in rows)
        {
            var v = versionOverrides?.GetValueOrDefault(row.LogId, version) ?? version;
            var id = MaterialId(row.LogId, v);
            var f = row.Fields;
            Material m;
            switch (row.Kind)
            {
                case "depth_datum":
                    m = new DepthDatum(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("name") ?? "",
                        f.GetValueOrDefault("basis") ?? "",
                        double.TryParse(f.GetValueOrDefault("offset_m"), NumberStyles.Float, CultureInfo.InvariantCulture, out var off) ? off : null);
                    break;
                case "section":
                    var datum = f.GetValueOrDefault("datum");
                    var top = Measure.Parse(Dimension.Depth, f.GetValueOrDefault("top"), f.GetValueOrDefault("top_unit"), datum, id, "top");
                    var bottom = Measure.Parse(Dimension.Depth, f.GetValueOrDefault("bottom"), f.GetValueOrDefault("bottom_unit"), datum, id, "bottom");
                    m = new CoreSection(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("code") ?? row.LogId, top, bottom, datum ?? "");
                    break;
                case "boundary":
                    var gapRaw = f.GetValueOrDefault("gap_thickness");
                    Measure? gap = gapRaw is null ? null
                        : Measure.Parse(Dimension.Depth, gapRaw, f.GetValueOrDefault("gap_unit"), null, id, "gap_thickness");
                    m = new SegmentBoundary(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("upper_section") ?? "",
                        f.GetValueOrDefault("lower_section") ?? "",
                        ParseKind(f.GetValueOrDefault("kind")),
                        gap, f.GetValueOrDefault("note"));
                    break;
                case "assay":
                    var adepth = Measure.Parse(Dimension.Depth, f.GetValueOrDefault("depth"), f.GetValueOrDefault("depth_unit"),
                        f.GetValueOrDefault("datum"), id, "depth");
                    var calibrated = string.Equals(f.GetValueOrDefault("calibrated"), "true", StringComparison.OrdinalIgnoreCase);
                    var adist = calibrated ? ParseDist(f) : null;
                    m = new RadiometricAssay(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("code") ?? row.LogId, adepth,
                        f.GetValueOrDefault("material") ?? "unknown", calibrated,
                        double.TryParse(f.GetValueOrDefault("reported_age"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ra) ? ra : null,
                        double.TryParse(f.GetValueOrDefault("reported_sigma"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rs) ? rs : null,
                        f.GetValueOrDefault("reported_age_unit"),
                        adist, f.GetValueOrDefault("reservoir"));
                    break;
                case "anchor":
                    var depth = Measure.Parse(Dimension.Depth, f.GetValueOrDefault("depth"), f.GetValueOrDefault("depth_unit"),
                        f.GetValueOrDefault("datum"), id, "depth");
                    m = new DirectAnchor(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("code") ?? row.LogId, depth,
                        ParseDist(f), f.GetValueOrDefault("basis"));
                    break;
                case "reservoir":
                    m = new ReservoirOffset(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("name") ?? row.LogId,
                        f.GetValueOrDefault("applies_to") ?? "any",
                        double.Parse(f.GetValueOrDefault("mean_years") ?? "0", CultureInfo.InvariantCulture),
                        double.Parse(f.GetValueOrDefault("sigma_years") ?? "0", CultureInfo.InvariantCulture));
                    break;
                case "event_layer":
                    var edepth = Measure.Parse(Dimension.Depth, f.GetValueOrDefault("depth"), f.GetValueOrDefault("depth_unit"),
                        f.GetValueOrDefault("datum"), id, "depth");
                    m = new EventLayer(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("code") ?? row.LogId, edepth,
                        f.GetValueOrDefault("lithology") ?? "");
                    break;
                case "event_correlation":
                    m = new EventCorrelation(id, row.LogId, v, row.Source, null,
                        f.GetValueOrDefault("event_log_id") ?? "",
                        f.GetValueOrDefault("external_name") ?? "",
                        f.GetValueOrDefault("external_basis") ?? "",
                        ParseDist(f));
                    break;
                default:
                    diags.Add(new ImportDiagnostic("IMPORT_UNKNOWN_KIND", "Error",
                        $"Unknown material kind '{row.Kind}'; row skipped but the source fragment is retained.", row.LogId, "kind", null));
                    continue;
            }
            foreach (var d in m.Validate().Concat(CollectMeasureDiags(m)))
                diags.Add(new ImportDiagnostic(d.Code, d.Severity.ToString(), d.Message, d.ObjectId, d.Field, d.SourceFragment));
            materials.Add(m);
        }
        return new ImportedBatch(batchId, materials, diags);
    }

    private static IEnumerable<Diagnostic> CollectMeasureDiags(Material m)
    {
        switch (m)
        {
            case CoreSection s:
                foreach (var d in s.Top.Diagnostics.Concat(s.Bottom.Diagnostics)) yield return d;
                break;
            case SegmentBoundary b when b.GapThickness is not null:
                foreach (var d in b.GapThickness.Diagnostics) yield return d;
                break;
            case RadiometricAssay a:
                foreach (var d in a.Depth.Diagnostics) yield return d;
                break;
            case DirectAnchor da:
                foreach (var d in da.Depth.Diagnostics) yield return d;
                break;
            case EventLayer e:
                foreach (var d in e.Depth.Diagnostics) yield return d;
                break;
        }
    }

    private static BoundaryKind ParseKind(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "unconformity" => BoundaryKind.Unconformity,
        "gap" => BoundaryKind.Gap,
        _ => BoundaryKind.Contact
    };

    public static AgeDistributionSpec ParseDist(IReadOnlyDictionary<string, string?> f)
    {
        var kind = f.GetValueOrDefault("dist") ?? "normal";
        double Num(string k) => double.Parse(f.GetValueOrDefault(k) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
        return kind switch
        {
            "lognormal" => new LogNormalSpec(Num("mulog"), Num("sigmalog")),
            "uniform" => new UniformSpec(Num("min"), Num("max")),
            "empirical" => new EmpiricalSpec(
                (f.GetValueOrDefault("samples") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray(),
                f.GetValueOrDefault("unit")),
            _ => new NormalSpec(Num("mean"), Num("sigma"))
        };
    }
}
