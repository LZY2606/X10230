using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StratAge.Core;

public sealed record NormalizedCase(
    CaseFile Case,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, ReservoirOffset> Reservoir,
    string Digest);

/// <summary>
/// Converts immutable raw material into the derived (canonical-unit) layer.
/// Raw text is never mutated; anything unconvertible stays in place with a
/// positioned diagnostic and is flagged unusable rather than defaulted.
/// </summary>
public static class Normalizer
{
    public static NormalizedCase Normalize(CaseFile cf)
    {
        var diags = new List<Diagnostic>();

        foreach (var b in cf.Boundaries)
        {
            b.Depth = Quant.Length(b.AtDepthRaw, cf.DepthDatum);
            if (!b.Depth.Usable)
                diags.Add(Diag(Severity.Warning, $"boundaries/{b.Id}/atDepthRaw",
                    b.Depth.DiagnosticCode!, b.Depth.Diagnostic!, "R-UNITS", b.Id));
        }

        foreach (var s in cf.Segments)
        {
            s.Top = Quant.Length(s.TopDepthRaw, cf.DepthDatum);
            s.Base = Quant.Length(s.BaseDepthRaw, cf.DepthDatum);
            if (!s.Top.Usable)
                diags.Add(Diag(Severity.Warning, $"segments/{s.Id}/topDepthRaw",
                    s.Top.DiagnosticCode!, s.Top.Diagnostic!, "R-UNITS", s.Id));
            if (!s.Base.Usable)
                diags.Add(Diag(Severity.Warning, $"segments/{s.Id}/baseDepthRaw",
                    s.Base.DiagnosticCode!, s.Base.Diagnostic!, "R-UNITS", s.Id));
            else if (s.Top.Usable && s.Base.Canonical!.Value < s.Top.Canonical!.Value)
                diags.Add(Diag(Severity.Warning, $"segments/{s.Id}", "SEGMENT_REVERSED",
                    $"Segment '{s.Name}' has base above its top in {cf.DepthDatum}; geometry needs review.",
                    "R-DEPTH", s.Id));
        }

        var res = cf.ReservoirOffsets.ToDictionary(r => r.Id);
        foreach (var e in cf.EventBeds)
        {
            e.Age = Quant.Time(e.AgeRaw, cf.ReferenceYear);
            if (!e.Age.Usable)
                diags.Add(Diag(Severity.Warning, $"eventBeds/{e.Id}/ageRaw",
                    e.Age.DiagnosticCode!, e.Age.Diagnostic!, "R-UNITS", e.Id));
        }

        foreach (var a in cf.Anchors)
        {
            a.Depth = Quant.Length(a.DepthRaw, cf.DepthDatum);
            if (!a.Depth.Usable)
                diags.Add(Diag(Severity.Error, $"anchors/{a.Id}/depthRaw",
                    a.Depth.DiagnosticCode!, a.Depth.Diagnostic!, "R-UNITS", a.Id));

            a.Age = Quant.Time(a.AgeRaw, cf.ReferenceYear);
            if (!a.Age.Usable)
                diags.Add(Diag(Severity.Error, $"anchors/{a.Id}/ageRaw",
                    a.Age.DiagnosticCode!, a.Age.Diagnostic!, "R-UNITS", a.Id));

            if (a.AgeSigmaRaw is not null)
            {
                a.AgeSigma = Quant.Time(a.AgeSigmaRaw, cf.ReferenceYear);
                if (!a.AgeSigma.Usable)
                    diags.Add(Diag(Severity.Warning, $"anchors/{a.Id}/ageSigmaRaw",
                        a.AgeSigma.DiagnosticCode!, a.AgeSigma.Diagnostic!, "R-UNITS", a.Id));
            }

            if (a.ReservoirOffsetId is not null && !res.ContainsKey(a.ReservoirOffsetId))
                diags.Add(Diag(Severity.Error, $"anchors/{a.Id}/reservoirOffsetId",
                    "MISSING_RESERVOIR",
                    $"Anchor {a.Id} references unknown reservoir offset '{a.ReservoirOffsetId}'.",
                    "R-RESERVOIR", a.Id));
            if (a.EventBedId is not null && !cf.EventBeds.Any(e => e.Id == a.EventBedId))
                diags.Add(Diag(Severity.Warning, $"anchors/{a.Id}/eventBedId",
                    "MISSING_EVENTBED",
                    $"Anchor {a.Id} references unknown event bed '{a.EventBedId}'.",
                    "R-EVENT", a.Id));
            if (!cf.Segments.Any(s => s.Id == a.SegmentId))
                diags.Add(Diag(Severity.Error, $"anchors/{a.Id}/segmentId",
                    "MISSING_SEGMENT",
                    $"Anchor {a.Id} is placed in unknown segment '{a.SegmentId}'.",
                    "R-DEPTH", a.Id));
        }

        var digest = Digest(cf);
        return new NormalizedCase(cf, diags, res, digest);
    }

    public static Diagnostic Diag(Severity sev, string loc, string code, string msg,
        string? rule, string? target) => new()
    {
        Severity = sev, Location = loc, Code = code, Message = msg, RuleId = rule, TargetId = target
    };

    public static string Digest(CaseFile cf)
    {
        var payload = new
        {
            cf.CaseId, cf.DepthDatum, cf.TimeBasis,
            anchors = cf.Anchors.Select(a => new { a.Id, a.DepthRaw, a.AgeRaw, a.AgeSigmaRaw,
                a.SegmentId, a.ReservoirOffsetId, a.TieGroup, a.Excluded, dist = DistributionSig(a.Distribution) }),
            segments = cf.Segments.Select(s => new { s.Id, s.TopDepthRaw, s.BaseDepthRaw, s.Overturned }),
            boundaries = cf.Boundaries.Select(b => new { b.Id, b.AtDepthRaw, kind = b.Kind.ToString() }),
            reservoir = cf.ReservoirOffsets.Select(r => new { r.Id, r.DeltaRKa, r.SigmaKa }),
            exclusions = cf.Exclusions.Select(e => new { e.Id, e.TargetId, e.RuleId })
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    private static object? DistributionSig(AgeDistribution? d) => d switch
    {
        GaussianAge g => new { t = "g", g.MeanKa, g.SigmaKa },
        StudentTAge t => new { t = "t", t.MeanKa, t.ScaleKa, t.Df },
        EmpiricalAge e => new { t = "e", n = e.SamplesKa.Length, sum = e.SamplesKa.Sum() },
        _ => null
    };
}
