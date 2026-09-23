using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgeDepth.Core;

/// <summary>Canonical serialization + SHA256 digests so equivalent inputs get stable identities.</summary>
public static class Canonical
{
    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Anchor(AgeAnchor a)
    {
        var d = a.Distribution;
        var sb = new StringBuilder();
        sb.Append(a.Id).Append('|').Append(Num(a.Depth)).Append('|').Append(a.Datum).Append('|');
        sb.Append(d.Kind).Append('|');
        sb.Append(Opt(d.Mean)).Append('|').Append(Opt(d.Sd)).Append('|');
        sb.Append(Opt(d.Min)).Append('|').Append(Opt(d.Max)).Append('|');
        sb.Append(Opt(d.Mu)).Append('|').Append(Opt(d.Sigma)).Append('|');
        sb.Append(d.Samples is null ? "-" : string.Join(',', d.Samples.Select(Num))).Append('|');
        sb.Append(d.Unit).Append('|').Append(Num(a.ReservoirOffsetYears)).Append('|');
        sb.Append(a.Material).Append('|').Append(a.Lab).Append('|').Append(a.Supersedes ?? "-");
        return sb.ToString();
    }

    public static string Section(Section s)
        => string.Join('|', s.Id, s.CoreId, Num(s.TopDepth), Num(s.BottomDepth), s.Datum, s.Lithology);

    public static string Boundary(Boundary b)
        => string.Join('|', b.Id, Num(b.Depth), b.Kind, b.Datum, b.Note);

    public static string EventLayer(EventLayer e)
        => string.Join('|', e.Id, Num(e.Depth), e.Datum, e.EventKind,
            e.CorrelationConfidence?.ToString("R", CultureInfo.InvariantCulture) ?? "-", e.Note);

    public static string Parameters(ModelParameters p)
        => string.Join('|', p.ParamVersion, p.Iterations, p.MaxIterations, Num(p.GridStep));

    private static string Opt(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Digest of the full model input. Ordering is normalized so equivalent inputs match.</summary>
    public static string InputDigest(
        IEnumerable<Section> sections,
        IEnumerable<Boundary> boundaries,
        IEnumerable<AgeAnchor> anchors,
        IEnumerable<EventLayer> events,
        ModelParameters p)
    {
        var sb = new StringBuilder();
        sb.Append("params:").Append(Parameters(p)).Append('\n');
        foreach (var s in sections.OrderBy(s => s.Id, StringComparer.Ordinal)) sb.Append("section:").Append(Section(s)).Append('\n');
        foreach (var b in boundaries.OrderBy(b => b.Id, StringComparer.Ordinal)) sb.Append("boundary:").Append(Boundary(b)).Append('\n');
        foreach (var a in anchors.OrderBy(a => a.Id, StringComparer.Ordinal)) sb.Append("anchor:").Append(Anchor(a)).Append('\n');
        foreach (var e in events.OrderBy(e => e.Id, StringComparer.Ordinal)) sb.Append("event:").Append(EventLayer(e)).Append('\n');
        return Sha256(sb.ToString());
    }
}
