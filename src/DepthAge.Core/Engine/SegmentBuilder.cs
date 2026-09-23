using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

/// <summary>
/// 把岩芯段组装成“不断层段”。缺口/不整合/断层是显式边界，
/// 任何年龄插值都不得跨越。倒置层段被标记为不可解，等待人工决定。
/// </summary>
public sealed class SegmentBuilder
{
    public List<ModelSegment> Build(ProjectState state, Dictionary<string, PreparedAnchor> prepared)
    {
        var segments = new List<ModelSegment>();
        var sections = state.Sections
            .OrderBy(s => s.TopDepthM)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        if (sections.Count == 0)
            return segments;

        var groupIndex = 0;
        var currentIds = new List<string> { sections[0].Id };
        var top = sections[0].TopDepthM;
        var bottom = sections[0].BottomDepthM;
        var upper = SegmentBoundary.Conformable;
        var inversion = sections[0].BottomBoundary == "Inversion";

        for (var i = 1; i < sections.Count; i++)
        {
            var section = sections[i];
            var boundary = Parse(sections[i - 1].BottomBoundary);
            if (boundary == SegmentBoundary.Conformable && sections[i].CoreId == sections[i - 1].CoreId)
            {
                currentIds.Add(section.Id);
                bottom = Math.Max(bottom, section.BottomDepthM);
                inversion |= section.BottomBoundary == "Inversion";
            }
            else
            {
                segments.Add(Make(state, prepared, groupIndex++, sections[i - 1].CoreId, top, bottom, upper, boundary, inversion, currentIds));
                currentIds = new List<string> { section.Id };
                top = section.TopDepthM;
                bottom = section.BottomDepthM;
                upper = boundary;
                inversion = section.BottomBoundary == "Inversion";
            }
        }
        var lastBoundary = Parse(sections[^1].BottomBoundary);
        segments.Add(Make(state, prepared, groupIndex, sections[^1].CoreId, top, bottom, upper, lastBoundary, inversion, currentIds));
        return segments;
    }

    private static ModelSegment Make(ProjectState state, Dictionary<string, PreparedAnchor> prepared,
        int index, string coreId, double topM, double bottomM, SegmentBoundary upper, SegmentBoundary lower,
        bool inverted, List<string> sectionIds)
    {
        var reason = lower switch
        {
            SegmentBoundary.Gap => $"显式缺口/缺芯边界：{state.Sections.First(s => s.Id == sectionIds[^1]).BoundaryNote ?? "未回收段"}，禁止跨边界插值",
            SegmentBoundary.Unconformity => $"不整合边界：{state.Sections.First(s => s.Id == sectionIds[^1]).BoundaryNote ?? "地层缺失"}，年龄在两侧不连续",
            SegmentBoundary.Fault => "断层边界：两侧深度-年龄关系断开",
            SegmentBoundary.Inversion => "倒置层段：层序翻转，需人工决定",
            _ => "连续整合段"
        };

        var anchorIds = prepared.Values
            .Where(p => p.Ready && p.Anchor.CoreId == coreId && p.DepthM >= topM - 1e-9 && p.DepthM <= bottomM + 1e-9)
            .OrderBy(p => p.DepthM).ThenBy(p => p.Anchor.Id, StringComparer.Ordinal)
            .Select(p => p.Anchor.Id)
            .ToList();

        var diagnostics = new List<string>();
        var solvable = true;
        if (inverted)
        {
            solvable = false;
            diagnostics.Add("倒置层段：沉积层序翻转，年龄-深度单调关系无唯一合法解，等待人工决定");
        }
        if (anchorIds.Count == 0)
            diagnostics.Add("段内无可用锚点：不能外推年龄，仅报告物理深度范围");

        return new ModelSegment
        {
            Id = $"seg-{index:D2}",
            CoreId = coreId,
            TopDepthM = topM,
            BottomDepthM = bottomM,
            UpperBoundary = upper,
            LowerBoundary = lower,
            BoundaryReason = reason,
            SectionIds = sectionIds,
            AnchorIds = anchorIds,
            Inverted = inverted,
            Solvable = solvable,
            Diagnostics = diagnostics
        };
    }

    public static SegmentBoundary Parse(string text) => text switch
    {
        "Gap" => SegmentBoundary.Gap,
        "Unconformity" => SegmentBoundary.Unconformity,
        "Fault" => SegmentBoundary.Fault,
        "Inversion" => SegmentBoundary.Inversion,
        _ => SegmentBoundary.Conformable
    };
}
