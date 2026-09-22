using System.Reflection;
using System.Text.Json;
using Chrono.Core.Model;
using Chrono.Core.Sourcing;

namespace Chrono.Core.Case;

/// <summary>
/// Loads the embedded offline case package. Raw representations are kept verbatim; normalization to
/// metres / parsed distributions happens here at the *derived* snapshot boundary, and values that
/// cannot be converted stay in place carrying a diagnostic.
/// </summary>
public static class CaseLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static CasePackage LoadEmbedded(string resourceName = "core-chronology-case.json")
    {
        var asm = Assembly.GetExecutingAssembly();
        var full = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"embedded case '{resourceName}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var stream = asm.GetManifestResourceStream(full)!;
        return JsonSerializer.Deserialize<CasePackage>(stream, JsonOpts)
            ?? throw new InvalidDataException("case package deserialized to null");
    }

    public static CasePackage ParseJson(string json)
        => JsonSerializer.Deserialize<CasePackage>(json, JsonOpts)
           ?? throw new InvalidDataException("case package deserialized to null");

    public static (BaselineIngestPayload Payload, List<Diagnostic> Diagnostics) ToBaseline(CasePackage pkg, string projectName)
    {
        var diags = new List<Diagnostic>();
        var payload = new BaselineIngestPayload
        {
            CaseId = pkg.CaseId,
            CaseVersion = pkg.Version,
            ProjectName = projectName,
            RuleVersion = pkg.RuleVersion,
            DatumId = "datum",
            DatumRaw = pkg.Datum,
            DatumFrame = pkg.DatumFrame,
        };

        foreach (var s in pkg.Segments)
        {
            var (top, dt) = FieldCodec.ParseDepth(s.Top, s.TopUnit);
            var (bot, db) = FieldCodec.ParseDepth(s.Bottom, s.BottomUnit);
            if (dt is not null) diags.Add(new Diagnostic("depth.unconvertible", Severity.Error, $"segment:{s.Id}.top", dt));
            if (db is not null) diags.Add(new Diagnostic("depth.unconvertible", Severity.Error, $"segment:{s.Id}.bottom", db));
            payload.Segments.Add(new SegmentSnapshot
            {
                Id = s.Id, Code = s.Code,
                RawTop = s.Top, RawTopUnit = s.TopUnit,
                RawBottom = s.Bottom, RawBottomUnit = s.BottomUnit,
                TopMetres = top ?? 0, BottomMetres = bot ?? 0,
                LowerContact = s.LowerContact, ContactRaw = s.ContactRaw,
                Lithology = s.Lithology,
            });
        }

        foreach (var a in pkg.Anchors)
        {
            var (depth, dr) = FieldCodec.ParseDepth(a.Depth, a.DepthUnit);
            var (dist, ar) = FieldCodec.ParseAge(a.Age);
            if (dr is not null)
                diags.Add(new Diagnostic("depth.unconvertible", Severity.Error, $"anchor:{a.Id}.depth", dr));
            if (ar is not null)
                diags.Add(new Diagnostic("age.unconvertible", Severity.Error, $"anchor:{a.Id}.age", ar,
                    new Dictionary<string, string> { ["raw"] = a.Age }));
            payload.Anchors.Add(new AnchorSnapshot
            {
                Id = a.Id, Label = a.Label,
                RawDepth = a.Depth, RawDepthUnit = a.DepthUnit, DepthMetres = depth,
                RawAge = a.Age,
                ReservoirOffsetId = a.ReservoirId, Material = a.Material,
                UnconvertibleReason = dr ?? ar,
            });
        }

        foreach (var e in pkg.Events)
        {
            var (depth, dr) = FieldCodec.ParseDepth(e.Depth, e.DepthUnit);
            if (dr is not null) diags.Add(new Diagnostic("depth.unconvertible", Severity.Error, $"event:{e.Id}.depth", dr));
            payload.Events.Add(new EventSnapshot
            {
                Id = e.Id, Code = e.Code,
                RawDepth = e.Depth, RawDepthUnit = e.DepthUnit, DepthMetres = depth,
                Correlation = e.Correlation, ExternalAgeReference = e.ExternalAgeReference,
            });
        }

        foreach (var r in pkg.Reservoirs)
            payload.Reservoirs.Add(new ReservoirSnapshot
            {
                Id = r.Id, MeanKa = r.MeanKa, SigmaKa = r.SigmaKa,
                ReferenceCurve = r.ReferenceCurve, RawText = r.Raw,
            });

        foreach (var x in pkg.Exclusions)
            payload.Exclusions.Add(new ExclusionSnapshot
            {
                Id = x.Id, AnchorId = x.AnchorId, Reason = x.Reason, DecidedBy = x.DecidedBy,
            });

        return (payload, diags);
    }
}
