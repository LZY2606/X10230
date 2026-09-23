using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgeDepth.Core;

/// <summary>Deterministic canonical rendering + sha256 digests for inputs and results.</summary>
public static class Canonical
{
    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Dist(AgeDistribution d) =>
        d.Kind + "(" + string.Join(",", d.P.Select(Num)) + ")";

    public static string InputDigest(ModelInput input)
    {
        var sb = new StringBuilder();
        sb.Append("core:").Append(Num(input.CoreTopM)).Append('-').Append(Num(input.CoreBottomM)).Append('\n');
        foreach (var a in input.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
            sb.Append("anchor:").Append(a.Id).Append('|').Append(Num(a.Depth.Value)).Append('|')
              .Append(a.Depth.Unit).Append('|').Append(a.Depth.Datum).Append('|')
              .Append(Dist(a.Dist)).Append('|').Append(a.Source).Append('\n');
        foreach (var b in input.Boundaries.OrderBy(b => b.Id, StringComparer.Ordinal))
            sb.Append("boundary:").Append(b.Id).Append('|').Append(Num(b.TopM)).Append('|')
              .Append(Num(b.BottomM)).Append('|').Append(b.Kind).Append('\n');
        foreach (var e in input.ExcludedAnchorIds.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append("excluded:").Append(e).Append('\n');
        foreach (var kv in input.ReservoirOffsetsBySource.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append("reservoir:").Append(kv.Key).Append('=').Append(Num(kv.Value)).Append('\n');
        return Sha256(sb.ToString());
    }

    public static string SegmentHash(SegmentResult seg)
    {
        var sb = new StringBuilder();
        sb.Append(seg.SegmentId).Append('|').Append(seg.Status).Append('|')
          .Append(Num(seg.TopM)).Append('|').Append(Num(seg.BottomM)).Append('\n');
        foreach (var c in seg.Candidates)
        {
            sb.Append("cand:").Append(c.Id).Append('|').Append(Num(c.Weight)).Append('|').Append(c.Status).Append('|');
            foreach (var k in c.Knots) sb.Append(Num(k.DepthM)).Append('@').Append(Num(k.AgeKa)).Append(',');
            sb.Append('\n');
        }
        foreach (var b in seg.Band)
            sb.Append("band:").Append(Num(b.DepthM)).Append('|').Append(Num(b.P05)).Append('|')
              .Append(Num(b.P50)).Append('|').Append(Num(b.P95)).Append('\n');
        foreach (var r in seg.Rates)
            sb.Append("rate:").Append(Num(r.TopM)).Append('|').Append(Num(r.BottomM)).Append('|')
              .Append(Num(r.MinMPerKa)).Append('|').Append(Num(r.MaxMPerKa)).Append('\n');
        foreach (var d in seg.Diagnostics)
            sb.Append("diag:").Append(d.Code).Append('|').Append(d.AnchorId).Append('|').Append(d.DepthM).Append('\n');
        return Sha256(sb.ToString());
    }
}
