using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chrono.Core.Materials;
using Chrono.Core.Quantities;

namespace Chrono.Core.Events;

/// <summary>
/// Round-trip serialization for immutable materials. Raw source representations and normalized
/// derived values are both carried, so import -> export -> import preserves identity & origin.
/// </summary>
public static class MaterialSerialization
{
    public static Material? Deserialize(JsonElement m)
    {
        string kind = m.GetProperty("kind").GetString()!;
        string id = m.GetProperty("id").GetString()!;
        string src = Str(m, "sourcePath", "");
        string file = Str(m, "sourceFile", "");
        int line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
        string core = Str(m, "coreId", "");
        bool superseded = m.TryGetProperty("superseded", out var s) && s.GetBoolean();
        string? supersededBy = m.TryGetProperty("supersededBy", out var sb) && sb.ValueKind == JsonValueKind.String
            ? sb.GetString() : null;

        Material? mat = kind switch
        {
            "depth_datum" => new DepthDatumMaterial(id, src, file, line, core,
                Str(m, "datumName", ""),
                Enum.Parse<DepthDirection>(Str(m, "direction", "PositiveDown"), true)),
            "core_segment" => new CoreSegmentMaterial(id, src, file, line, core,
                Str(m, "name", ""),
                Str(m, "topRaw", null), Str(m, "bottomRaw", null),
                Str(m, "depthUnit", ""), Str(m, "datumName", ""),
                Enum.Parse<DepthDirection>(Str(m, "direction", "PositiveDown"), true),
                Enum.Parse<SegmentStructure>(Str(m, "structure", "Continuous"), true),
                ReadDepth(m, "topCm"), ReadDepth(m, "bottomCm")),
            "gap" => new GapMaterial(id, src, file, line, core,
                Enum.Parse<GapKind>(Str(m, "gapType", "Unconformity"), true),
                Str(m, "depthRaw", null), Str(m, "depthUnit", ""),
                Str(m, "datumName", ""),
                Enum.Parse<DepthDirection>(Str(m, "direction", "PositiveDown"), true),
                Str(m, "note", ""), ReadDepth(m, "atCm")),
            "radiometric_date" => new RadiometricDateMaterial(id, src, file, line, core,
                Str(m, "labCode", ""), Str(m, "depthRaw", null), Str(m, "depthUnit", ""),
                Str(m, "datumName", ""),
                Enum.Parse<DepthDirection>(Str(m, "direction", "PositiveDown"), true),
                m.TryGetProperty("measuredAge", out var age) ? ReadDist(age) : null,
                m.TryGetProperty("reservoirOffsetId", out var ro) && ro.ValueKind == JsonValueKind.String
                    ? ro.GetString() : null,
                ReadDepth(m, "atCm"))
                {
                    Excluded = Bool(m, "excluded"),
                    ExclusionReason = Str(m, "exclusionReason", null)
                },
            "reservoir_offset" => new ReservoirMaterial(id, src, file, line, core,
                Str(m, "name", ""),
                m.TryGetProperty("offsetDistribution", out var od) ? ReadDist(od) : new NormalAgeDistribution(0, 0, "BP1950"),
                !m.TryGetProperty("enabled", out var en) || en.GetBoolean()),
            "event_tie" => new EventTieMaterial(id, src, file, line, core,
                Str(m, "eventCode", ""), Str(m, "depthRaw", null), Str(m, "depthUnit", ""),
                Str(m, "datumName", ""),
                Enum.Parse<DepthDirection>(Str(m, "direction", "PositiveDown"), true),
                m.TryGetProperty("correlatedAge", out var ca) ? ReadDist(ca) : null,
                Str(m, "correlationBasis", ""), ReadDepth(m, "atCm"))
                {
                    Excluded = Bool(m, "excluded"),
                    ExclusionReason = Str(m, "exclusionReason", null),
                    CorrelationGroup = Str(m, "correlationGroup", null)
                },
            "exclusion_decision" => new ExclusionDecisionMaterial(id, src, file, line,
                Str(m, "targetMaterialId", ""), Str(m, "reason", ""), Str(m, "decidedBy", "")),
            _ => null
        };

        if (mat is not null && superseded) mat = mat with { Superseded = true, SupersededBy = supersededBy };
        if (mat is not null && m.TryGetProperty("diagnostics", out var ds))
            foreach (var d in ds.EnumerateArray())
                mat.Diagnostics.Add(new Diagnostic(d.GetProperty("code").GetString()!,
                    d.GetProperty("message").GetString()!, d.GetProperty("path").GetString()!));
        return mat;
    }

    private static string Str(JsonElement m, string name, string? fallback)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback ?? "";
    private static bool Bool(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True && v.GetBoolean();
    private static Depth? ReadDepth(JsonElement m, string name)
        => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? new Depth(v.GetDouble()) : null;

    private static IAgeDistribution ReadDist(JsonElement el)
    {
        string kind = el.GetProperty("kind").GetString()!;
        string datum = el.TryGetProperty("datum", out var dd) ? dd.GetString() ?? "BP1950" : "BP1950";
        return kind switch
        {
            "normal" => new NormalAgeDistribution(el.GetProperty("mean").GetDouble(),
                el.GetProperty("sigma").GetDouble(), datum),
            "student_t" => new StudentTAgeDistribution(el.GetProperty("location").GetDouble(),
                el.GetProperty("scale").GetDouble(), el.GetProperty("df").GetDouble(), datum),
            "mixture" => new MixtureAgeDistribution(
                el.GetProperty("components").EnumerateArray().Select(c => new MixtureComponent(
                    c.GetProperty("weight").GetDouble(), ReadDist(c.GetProperty("distribution")))).ToList(),
                datum),
            "reservoir_corrected" => new Model.ShiftedDistribution(
                ReadDist(el.GetProperty("measured")), ReadDist(el.GetProperty("offset"))),
            _ => throw new FormatException($"unknown distribution kind {kind}")
        };
    }

    public static Dictionary<string, object?> ToMap(Material m)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = m.Id, ["kind"] = m.Kind, ["sourcePath"] = m.SourcePath,
            ["sourceFile"] = m.SourceFile, ["line"] = m.Line, ["coreId"] = Field(m, "CoreId"),
            ["superseded"] = m.Superseded, ["supersededBy"] = m.SupersededBy,
            ["diagnostics"] = m.Diagnostics
                .Select(d => new Dictionary<string, object?> { ["code"] = d.Code, ["message"] = d.Message, ["path"] = d.Path }).ToList()
        };
        switch (m)
        {
            case DepthDatumMaterial d:
                map["datumName"] = d.DatumName; map["direction"] = d.Direction.ToString(); break;
            case CoreSegmentMaterial s:
                map["name"] = s.Name; map["topRaw"] = s.TopRaw; map["bottomRaw"] = s.BottomRaw;
                map["depthUnit"] = s.DepthUnit; map["datumName"] = s.DatumName;
                map["direction"] = s.Direction.ToString(); map["structure"] = s.Structure.ToString();
                map["topCm"] = s.TopCm?.Cm; map["bottomCm"] = s.BottomCm?.Cm; break;
            case GapMaterial g:
                map["gapType"] = g.GapType.ToString(); map["depthRaw"] = g.DepthRaw;
                map["depthUnit"] = g.DepthUnit; map["datumName"] = g.DatumName;
                map["direction"] = g.Direction.ToString(); map["note"] = g.Note;
                map["atCm"] = g.AtCm?.Cm; break;
            case RadiometricDateMaterial r:
                map["labCode"] = r.LabCode; map["depthRaw"] = r.DepthRaw; map["depthUnit"] = r.DepthUnit;
                map["datumName"] = r.DatumName; map["direction"] = r.Direction.ToString();
                map["measuredAge"] = DistMap(r.MeasuredAge); map["reservoirOffsetId"] = r.ReservoirOffsetId;
                map["atCm"] = r.AtCm?.Cm; map["excluded"] = r.Excluded; map["exclusionReason"] = r.ExclusionReason; break;
            case ReservoirMaterial ro:
                map["name"] = ro.Name; map["offsetDistribution"] = DistMap(ro.OffsetDistribution);
                map["enabled"] = ro.Enabled; break;
            case EventTieMaterial e:
                map["eventCode"] = e.EventCode; map["depthRaw"] = e.DepthRaw; map["depthUnit"] = e.DepthUnit;
                map["datumName"] = e.DatumName; map["direction"] = e.Direction.ToString();
                map["correlatedAge"] = DistMap(e.CorrelatedAge); map["correlationBasis"] = e.CorrelationBasis;
                map["atCm"] = e.AtCm?.Cm; map["excluded"] = e.Excluded;
                map["exclusionReason"] = e.ExclusionReason; map["correlationGroup"] = e.CorrelationGroup; break;
            case ExclusionDecisionMaterial x:
                map["targetMaterialId"] = x.TargetMaterialId; map["reason"] = x.Reason;
                map["decidedBy"] = x.DecidedBy; break;
        }
        return map;
    }

    private static object? Field(Material m, string name) => typeof(Material).IsAssignableFrom(m.GetType())
        ? m.GetType().GetProperty(name)?.GetValue(m) : null;

    public static Dictionary<string, object?>? DistMap(IAgeDistribution? d)
    {
        switch (d)
        {
            case null: return null;
            case NormalAgeDistribution n:
                return new() { ["kind"] = "normal", ["datum"] = n.Datum, ["mean"] = n.Mean, ["sigma"] = n.Sigma };
            case StudentTAgeDistribution t:
                return new() { ["kind"] = "student_t", ["datum"] = t.Datum, ["location"] = t.Location,
                    ["scale"] = t.Scale, ["df"] = t.DegreesFreedom };
            case MixtureAgeDistribution mix:
                return new() { ["kind"] = "mixture", ["datum"] = mix.Datum,
                    ["components"] = mix.Components
                        .Select(c => new Dictionary<string, object?> { ["weight"] = c.Weight,
                            ["distribution"] = DistMap(c.Distribution) }).ToList() };
            case Model.ShiftedDistribution sh:
                return new() { ["kind"] = "reservoir_corrected", ["datum"] = sh.Datum,
                    ["measured"] = DistMap(sh.Measured), ["offset"] = DistMap(sh.Offset) };
            default: return new() { ["kind"] = d.Kind, ["datum"] = d.Datum };
        }
    }
}
