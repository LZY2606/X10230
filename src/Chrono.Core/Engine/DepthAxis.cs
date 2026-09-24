using Chrono.Core.Domain;
using Chrono.Core.Models;

namespace Chrono.Core.Engine;

/// <summary>One uninterrupted stretch between explicit barriers. Age is only
/// required to be monotonic inside such a section; gaps and unconformities are
/// boundaries, never interpolated across.</summary>
public sealed record UnbrokenSection(string Id, double TopM, double BottomM, bool HasInversion,
    IReadOnlyList<string> SegmentIds, IReadOnlyList<string> BarrierIds);

public sealed record DepthAxis(IReadOnlyList<UnbrokenSection> Sections, IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record SegRow(string Id, double Top, double Bottom, SegmentKind Kind, string Note);

public static class DepthAxisBuilder
{
    public static DepthAxis Build(AppState s)
    {
        var diags = new List<Diagnostic>();
        if (s.Datum is null || !s.Datum.Depth.IsConvertible)
            diags.Add(new Diagnostic("DATUM-000", "缺少可转换的深度基准，无法建立深度轴", "datum"));
        var sign = s.Datum?.PositiveDowncore == false ? -1 : 1;

        var segs = s.Segments.Values
            .Where(x => x.Top.IsConvertible && x.Bottom.IsConvertible)
            .Select(x => new SegRow(x.Id, sign * x.Top.NormValue!.Value, sign * x.Bottom.NormValue!.Value, x.Kind, x.Note))
            .OrderBy(x => x.Top).ThenBy(x => x.Bottom).ToList();

        var dupGroups = segs.GroupBy(x => (Math.Round(x.Top, 6), Math.Round(x.Bottom, 6)))
            .Where(g => g.Count() > 1).ToList();
        foreach (var g in dupGroups)
            diags.Add(new Diagnostic("DEPTH-DUP-SEG", $"深度重复的分段：{string.Join(",", g.Select(x => x.Id))}",
                $"segments:{string.Join(",", g.Select(x => x.Id))}", Severity.Warning));

        var sections = new List<UnbrokenSection>();
        var barriers = new List<string>();
        var members = new List<string>();
        bool inversion = false;
        int n = 0; string secId = $"sec-{++n}"; double? secTop = null; double secBottom = 0;
        double prevBottom = double.NegativeInfinity;

        void Close()
        {
            if (members.Count > 0)
                sections.Add(new UnbrokenSection(secId, secTop ?? 0, secBottom, inversion, members.ToList(), barriers.ToList()));
        }

        foreach (var seg in segs)
        {
            if (seg.Kind is SegmentKind.Gap or SegmentKind.Unconformity)
            {
                Close();
                barriers.Add(seg.Id);
                members = new List<string>(); inversion = false;
                secId = $"sec-{++n}"; secTop = null;
                prevBottom = double.NegativeInfinity;
                continue;
            }
            if (seg.Top < prevBottom - 1e-9)
            {
                inversion = true;
                diags.Add(new Diagnostic("INVERSION-SEC",
                    $"倒置层段：{seg.Id} 顶部 {seg.Top}m 早于上一层底部 {prevBottom}m（深度倒序）",
                    $"segment:{seg.Id}", Severity.Warning));
            }
            if (seg.Kind == SegmentKind.Inverted) inversion = true;
            secTop ??= seg.Top; secBottom = seg.Bottom; members.Add(seg.Id);
            prevBottom = Math.Max(prevBottom, seg.Bottom);
        }
        Close();
        return new DepthAxis(sections, diags);
    }

    public static UnbrokenSection? SectionAt(DepthAxis axis, double depthM) =>
        axis.Sections.FirstOrDefault(x => depthM >= x.TopM - 1e-9 && depthM <= x.BottomM + 1e-9);
}
