using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

public sealed class MaterialRecord
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public JsonElement Payload { get; init; }
    public int Seq { get; init; }
    public bool Active { get; init; } = true;
}

public sealed class CandidateResult
{
    public string CandidateKey { get; set; } = "";
    public double Score { get; set; }
    public bool EqualBest { get; set; }
    public List<SectionOutcome> Sections { get; set; } = new();
}

public sealed class SectionOutcome
{
    public string SectionId { get; set; } = "";
    public string Status { get; set; } = "ok"; // ok | inverted | unsolvable | empty
    public string? Reason { get; set; }
    public List<RateInterval> Rates { get; set; } = new();
    public List<BandNode> Band { get; set; } = new();
}

public sealed class RateInterval
{
    public double FromDepthM { get; set; }
    public double ToDepthM { get; set; }
    public double MedianCMperKY { get; set; }
    public double Q05CMperKY { get; set; }
    public double Q95CMperKY { get; set; }
}

public sealed class BandNode
{
    public double DepthM { get; set; }
    public double Q05AgeKA { get; set; }
    public double MedianAgeKA { get; set; }
    public double Q95AgeKA { get; set; }
}

public sealed class RunResult
{
    public required string Id { get; init; }
    public required string InputDigest { get; init; }
    public required RuleSet Rules { get; init; }
    public RunStatus Status { get; set; }
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<CandidateResult> Candidates { get; set; } = new();
    public List<string> MaterialIds { get; set; } = new();
    public Dictionary<string, List<string>> SectionDependencies { get; set; } = new();
    public Dictionary<string, string> SectionDigests { get; set; } = new();
    public CaseView? View { get; set; }
}

public static class AgeDepthEngine
{
    public const string DepthUnit = "m";
    public const string AgeUnit = "cal ka BP";

    private static readonly Dictionary<string, double> DepthFactor = new()
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["metres"] = 1.0,
        ["cm"] = 0.01, ["millimeter"] = 0.001, ["mm"] = 0.001
    };
    private static readonly Dictionary<string, double> AgeFactor = new()
    {
        ["cal ka bp"] = 1.0, ["ka bp"] = 1.0, ["ka"] = 1.0,
        ["cal yr bp"] = 0.001, ["yr bp"] = 0.001, ["years bp"] = 0.001, ["yr"] = 0.001,
        ["bp"] = 0.001
    };

    public static double StableFraction(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return (double)BitConverter.ToUInt64(h, 0) / ulong.MaxValue;
    }

    public static string StableId(string prefix, string canonical)
        => $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant()}";

    public static string Digest(IEnumerable<MaterialRecord> mats)
    {
        var sb = new StringBuilder();
        foreach (var m in mats.Where(m => m.Active).OrderBy(m => m.Id, StringComparer.Ordinal))
            sb.Append(m.Id).Append('=').Append(m.Payload.GetRawText()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }

    public static RawQuantity NormalizeDepth(RawQuantity q)
    {
        if (q.Value.HasValue) return q;
        var d = Parse(q.Raw, DepthFactor);
        if (d is null)
        {
            q.Diagnostic = $"unconvertible depth value '{q.Raw}' (unit '{q.Unit ?? "?"}'); kept at source, not dropped for plotting";
            return q;
        }
        q.Value = d;
        q.NormalizedUnit = DepthUnit;
        return q;
    }

    public static RawQuantity NormalizeAge(RawQuantity q)
    {
        if (q.Value.HasValue) return q;
        var d = Parse(q.Raw, AgeFactor);
        if (d is null)
        {
            q.Diagnostic = $"unconvertible age value '{q.Raw}' (unit '{q.Unit ?? "?"}'); kept at source";
            return q;
        }
        q.Value = d;
        q.NormalizedUnit = AgeUnit;
        return q;
    }

    private static double? Parse(string raw, Dictionary<string, double> factor)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        var i = 0;
        while (i < raw.Length && (char.IsDigit(raw[i]) || raw[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        if (i == 0) return null;
        if (!double.TryParse(raw[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return null;
        var rest = raw[i..].Trim().ToLowerInvariant();
        if (rest.Length == 0 && factor.TryGetValue(raw.ToLowerInvariant(), out var f)) return v * f;
        if (factor.TryGetValue(rest, out var f2)) return v * f2;
        if (factor.TryGetValue(raw.ToLowerInvariant(), out var f3)) return v * f3;
        return null;
    }

    // ---- case assembly from immutable materials ----

    public static CaseView BuildCase(IEnumerable<MaterialRecord> mats, List<Diagnostic>? diags = null)
    {
        var list = mats.Where(m => m.Active).ToList();
        diags ??= new List<Diagnostic>();
        var corrupt = false;
        string coreId = "CORE-1";

        var datums = new List<(string id, DepthDatum m)>();
        var segments = new List<(string id, SegmentMaterial m)>();
        var anchors = new List<(string id, AnchorMaterial m)>();
        var offsets = new Dictionary<string, ReservoirOffset>();
        var events = new List<EventLayerMaterial>();
        var exclusions = new List<ExclusionMaterial>();

        foreach (var mr in list)
        {
            object? parsed;
            try
            {
                parsed = mr.Kind switch
                {
                    "depth_datum" => mr.Payload.Deserialize<DepthDatum>(),
                    "segment" => mr.Payload.Deserialize<SegmentMaterial>(),
                    "anchor" => mr.Payload.Deserialize<AnchorMaterial>(),
                    "reservoir_offset" => mr.Payload.Deserialize<ReservoirOffset>(),
                    "event_layer" => mr.Payload.Deserialize<EventLayerMaterial>(),
                    "exclusion" => mr.Payload.Deserialize<ExclusionMaterial>(),
                    _ => null
                };
            }
            catch (JsonException ex)
            {
                diags.Add(new Diagnostic(mr.Id, null, $"format corrupt: {ex.Message}", "error"));
                corrupt = true;
                continue;
            }
            switch (parsed)
            {
                case DepthDatum d: datums.Add((mr.Id, d)); coreId = d.CoreId; break;
                case SegmentMaterial s: segments.Add((mr.Id, s)); break;
                case AnchorMaterial a: anchors.Add((mr.Id, a)); break;
                case ReservoirOffset r: offsets[r.OffsetId] = r; break;
                case EventLayerMaterial e: events.Add(e); break;
                case ExclusionMaterial x: exclusions.Add(x); break;
            }
        }

        // conflicting duplicate depth datums
        foreach (var g in datums.GroupBy(d => NormalizeDepth(d.m.Depth).Value))
        {
            if (g.Count() > 1 && g.Key is not null)
                diags.Add(new Diagnostic(g.First().id, "depth", $"duplicate depth datum {g.Key:F3} m in {string.Join(",", g.Select(x => x.id))}", "warning"));
        }

        // normalize segments; unconvertible values stay in place with a diagnostic
        var seg = new List<(string id, SegmentMaterial m, double top, double bottom)>();
        foreach (var (id, s) in segments)
        {
            NormalizeDepth(s.Top); NormalizeDepth(s.Bottom);
            if (s.Top.Unconvertible || s.Top.Value is null)
            { diags.Add(new Diagnostic(id, "top", s.Top.Diagnostic ?? "missing top depth", "error")); continue; }
            if (s.Bottom.Unconvertible || s.Bottom.Value is null)
            { diags.Add(new Diagnostic(id, "bottom", s.Bottom.Diagnostic ?? "missing bottom depth", "error")); continue; }
            if (s.Top.Value.Value >= s.Bottom.Value.Value)
                diags.Add(new Diagnostic(id, "top", $"inverted segment bounds {s.Top.Value:F3}>={s.Bottom.Value:F3} m", "error"));
            seg.Add((id, s, s.Top.Value.Value, s.Bottom.Value.Value));
        }
        seg.Sort((a, b) => a.top.CompareTo(b.top));

        // build continuous sections, splitting at explicit gaps / unconformities
        var sections = new List<SectionView>();
        var currentIds = new List<string> { seg[0].id };
        var currentSegs = new List<(string id, SegmentMaterial m, double top, double bottom)> { seg[0] };
        for (var k = 1; k < seg.Count; k++)
        {
            var prev = seg[k - 1];
            var cur = seg[k];
            var gap = cur.top - prev.bottom;
            if (prev.m.Below == BoundaryKind.Continuous && Math.Abs(gap) < 1e-9)
            {
                currentIds.Add(cur.id); currentSegs.Add(cur);
            }
            else
            {
                var kind = Math.Abs(gap) > 1e-9 && prev.m.Below == BoundaryKind.Continuous
                    ? BoundaryKind.Gap : prev.m.Below;
                sections.Add(MakeSection(sections.Count, currentSegs, kind));
                currentIds = new List<string> { cur.id };
                currentSegs = new List<(string, SegmentMaterial, double, double)> { cur };
            }
        }
        if (currentSegs.Count > 0)
            sections.Add(MakeSection(sections.Count, currentSegs, BoundaryKind.Continuous));

        // anchors: apply recorded exclusions, normalize, flag duplicate depths
        var excludedTargets = new HashSet<string>(exclusions.Select(x => x.TargetMaterialId));
        var ancViews = new List<AnchorView>();
        foreach (var (id, a) in anchors)
        {
            NormalizeDepth(a.Depth);
            if (a.Depth.Unconvertible || a.Depth.Value is null)
            { diags.Add(new Diagnostic(id, "depth", a.Depth.Diagnostic ?? "missing anchor depth", "error")); continue; }
            if (a.Excluded || excludedTargets.Contains(id))
            {
                diags.Add(new Diagnostic(id, null, $"excluded ({a.ExcludeReason ?? "exclusion material"}); record retained", "info"));
                continue;
            }
            NormalizeDistribution(a.Age, id, diags, a.ReservoirOffsetId is not null && offsets.TryGetValue(a.ReservoirOffsetId, out var ro) ? ro : null);
            if (a.Age.Samples is null && (a.Age.Mean is null || !TryRange(a.Age, out _)))
            { diags.Add(new Diagnostic(id, "age", "age distribution unusable: no samples and no valid parameters", "error")); continue; }
            ancViews.Add(new AnchorView
            {
                AnchorId = id, DepthM = a.Depth.Value.Value, Age = a.Age,
                Method = a.Method, EventLayerId = a.EventLayerId
            });
        }
        foreach (var g in ancViews.GroupBy(x => x.DepthM).Where(g => g.Count() > 1))
        {
            var same = g.Select(x => MedianAge(x.Age)).Distinct().Count() == 1;
            diags.Add(new Diagnostic(
                string.Join(",", g.Select(x => x.AnchorId).OrderBy(x => x, StringComparer.Ordinal)),
                "depth",
                same ? $"duplicate depth {g.Key:F3} m with identical distribution (retained once)"
                     : $"tied anchors at depth {g.Key:F3} m with competing distributions; all legal orderings kept as candidates",
                same ? "warning" : "info"));
        }

        foreach (var s in sections)
            s.Anchors.AddRange(ancViews.Where(a => a.DepthM >= s.TopM - 1e-9 && a.DepthM <= s.BottomM + 1e-9)
                                       .OrderBy(a => a.DepthM).ThenBy(a => a.AnchorId, StringComparer.Ordinal));
        return new CaseView { CoreId = coreId, Sections = sections, EventLayers = events, Diagnostics = diags, FormatCorrupt = corrupt };
    }
