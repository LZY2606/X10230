using Chrono.Core.Domain;
using Chrono.Core.Models;

namespace Chrono.Core.Case;

public sealed record ImportResult(
    OutcomeKind Kind,
    DepthDatum? Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments);

/// <summary>
/// Parser for the domain case pack "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// This is deliberately a stratigraphic DSL, not a generic spreadsheet:
/// sections encode depth datum, segment gaps, radiometric distributions,
/// reservoir offsets, event correlations and retained exclusions.
///
/// Every domain quantity keeps its raw text; normalization happens on the
/// derived layer. An unconvertible value stays at its line with a diagnostic.
/// </summary>
public static class CaseFormat
{
    public static ImportResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Fail(OutcomeKind.FormatCorrupt, new Diagnostic("CASE-EMPTY", "案例文本为空", "input"));

        var lines = text.Replace("\r\n", "\n").Split('\n');
        DepthDatum? datum = null;
        var segs = new List<CoreSegment>();
        var anchors = new List<AgeAnchor>();
        var res = new List<ReservoirCorrection>();
        var events = new List<EventLayer>();
        var exclusions = new List<ExclusionDecision>();
        var diags = new List<Diagnostic>();
        var judgments = new List<Judgment>();
        string? section = null;
        int lineNo = 0;

        foreach (var rawLine in lines)
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim().ToUpperInvariant(); continue; }
            try
            {
                switch (section)
                {
                    case "DATUM": ParseDatum(line, ref datum); break;
                    case "SEGMENTS": ParseSegment(line, segs, lineNo, diags); break;
                    case "RESERVOIRS": ParseReservoir(line, res); break;
                    case "EVENTS": ParseEvent(line, events); break;
                    case "ANCHORS": ParseAnchor(line, anchors, res, diags, judgments); break;
                    case "EXCLUSIONS": ParseExclusion(line, exclusions); break;
                    default:
                        diags.Add(new Diagnostic("CASE-SECTION", $"未知段落标记或内容出现在段落之外：'{line}'", $"L{lineNo}", Severity.Warning));
                        break;
                }
            }
            catch (CaseFormatException ex)
            {
                return Fail(OutcomeKind.FormatCorrupt, new Diagnostic("CASE-SYNTAX", ex.Message, $"L{lineNo}"));
            }
        }

        if (datum is null)
            return Fail(OutcomeKind.FormatCorrupt, new Diagnostic("CASE-DATUM", "缺少 [DATUM] 深度基准段落", "datum"));

        ApplyExclusions(anchors, exclusions, diags, judgments);
        FlagDuplicateDepths(anchors, diags, judgments);
        var kind = diags.Any(d => d.Severity == Severity.Error)
            ? OutcomeKind.SemanticImpossible : OutcomeKind.Ok;
        return new ImportResult(kind, datum, segs, anchors, res, events, exclusions, diags, judgments);
    }

    private sealed class CaseFormatException : Exception { public CaseFormatException(string m) : base(m) { } }

    private static string[] Fields(string line)
    {
        var parts = line.Split('|');
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new CaseFormatException($"字段为空（不允许空值伪装成功）：'{line}'");
        return parts.Select(p => p.Trim()).ToArray();
    }

    private static void ParseDatum(string line, ref DepthDatum? datum)
    {
        var f = Fields(line);
        if (f.Length < 4) throw new CaseFormatException("DATUM 需要: id|名称|深度|坐标基准(正向下=down)");
        datum = new DepthDatum(f[0], f[1], Qty.Parse(f[2], "depth"), f[3],
            f[3].Contains("down", StringComparison.OrdinalIgnoreCase), line);
    }

    private static void ParseSegment(string line, List<CoreSegment> segs, int lineNo, List<Diagnostic> diags)
    {
        var f = Fields(line);
        if (f.Length < 5) throw new CaseFormatException("SEGMENTS 需要: id|名称|顶深|底深|类型(continuous/gap/unconformity/inverted)[|备注]");
        if (!Enum.TryParse<SegmentKind>(f[4], true, out var kind))
            throw new CaseFormatException($"未知分段类型 '{f[4]}'");
        segs.Add(new CoreSegment(f[0], f[1], Qty.Parse(f[2], "depth"), Qty.Parse(f[3], "depth"),
            kind, f.Length > 5 ? f[5] : "", line));
    }

    private static void ParseReservoir(string line, List<ReservoirCorrection> res)
    {
        var f = Fields(line);
        if (f.Length < 4) throw new CaseFormatException("RESERVOIRS 需要: id|锚点id|偏移量|依据");
        res.Add(new ReservoirCorrection(f[0], f[1], Qty.Parse(f[2], "offset"), f[3], line));
    }

    private static void ParseEvent(string line, List<EventLayer> events)
    {
        var f = Fields(line);
        if (f.Length < 4) throw new CaseFormatException("EVENTS 需要: id|名称|关联锚点id或-|外部年龄或-|依据");
        var age = f[3] == "-" ? null : Qty.Parse(f[3], "age");
        events.Add(new EventLayer(f[0], f[1], f[2] == "-" ? null : f[2], age, f[4..] is { Length: > 0 } ? string.Join("|", f[4..]) : "", line));
    }

    private static void ParseAnchor(string line, List<AgeAnchor> anchors,
        List<ReservoirCorrection> res, List<Diagnostic> diags, List<Judgment> judgments)
    {
        var f = Fields(line);
        // id|深度|年龄表达式|材料|年代基准|储层id或-|事件id或-
        if (f.Length < 7) throw new CaseFormatException("ANCHORS 需要: id|深度|年龄表达式|材料|年代基准|储层id或-|事件id或-");
        var depth = Qty.Parse(f[1], "depth");
        var (dist, ageDiags) = AgeExpression.Parse(f[2]);
        var anchorDiags = new List<Diagnostic>();
        if (!depth.IsConvertible)
            anchorDiags.Add(new Diagnostic("DEPTH-UNCONV", depth.ConvertError ?? "深度无法转换", $"anchor:{f[0]}", Severity.Warning));
        anchorDiags.AddRange(ageDiags.Select(d => d with { Location = $"anchor:{f[0]}" }));
        var status = (!depth.IsConvertible || ageDiags.Count > 0) ? AnchorStatus.Unconvertible : AnchorStatus.Active;
        anchors.Add(new AgeAnchor(f[0], f[0], depth, dist, f[3], f[4],
            f[5] == "-" ? null : f[5], f[6] == "-" ? null : f[6],
            status, null, anchorDiags, line));
    }

    private static void ParseExclusion(string line, List<ExclusionDecision> exclusions)
    {
        var f = Fields(line);
        if (f.Length < 5) throw new CaseFormatException("EXCLUSIONS 需要: id|目标id|true/false|决定人|理由");
        if (!bool.TryParse(f[2], out var excluded)) throw new CaseFormatException($"排除标记必须为 true/false：'{f[2]}'");
        exclusions.Add(new ExclusionDecision(f[0], f[1], string.Join("|", f[4..]), excluded, f[3], line));
    }

    private static void ApplyExclusions(List<AgeAnchor> anchors, List<ExclusionDecision> exclusions,
        List<Diagnostic> diags, List<Judgment> judgments)
    {
        foreach (var x in exclusions)
        {
            var a = anchors.FirstOrDefault(z => z.Id == x.TargetId);
            if (a is null)
            {
                diags.Add(new Diagnostic("EXCL-TARGET", $"排除决定指向不存在的锚点 {x.TargetId}", $"exclusion:{x.Id}"));
                continue;
            }
            if (x.Excluded)
            {
                var idx = anchors.IndexOf(a);
                anchors[idx] = a with { Status = AnchorStatus.Excluded, ExclusionId = x.Id };
                judgments.Add(Judgment.Create(JudgmentCode.ExclusionRetained, true,
                    $"排除决定 {x.Id} 被保留：锚点 {x.TargetId} 不参与拟合但材料仍在档", new[] { x.TargetId, x.Id }));
            }
        }
    }

    private static void FlagDuplicateDepths(List<AgeAnchor> anchors, List<Diagnostic> diags, List<Judgment> judgments)
    {
        // Only convertible depths can be meaningfully compared.
        var groups = anchors.Where(a => a.Depth.IsConvertible && a.Status != AnchorStatus.Excluded)
            .GroupBy(a => Math.Round(a.Depth.NormValue!.Value, 3))
            .Where(g => g.Count() > 1).ToList();
        foreach (var g in groups)
        {
            bool distinctMaterial = g.Select(a => a.Material).Distinct().Count() > 1;
            if (!distinctMaterial)
            {
                // same material twice at the same depth is the "depth duplicate" data error.
                foreach (var a in g)
                {
                    var idx = anchors.IndexOf(a);
                    anchors[idx] = a with { Status = AnchorStatus.Duplicate };
                }
                diags.Add(new Diagnostic("DEPTH-DUP-ANCHOR",
                    $"深度 {g.Key}m 出现同材料重复锚点（{string.Join(",", g.Select(a => a.Id))}），需人工处理",
                    $"anchors:{string.Join(",", g.Select(a => a.Id))}"));
            }
            // distinct materials at equal depth -> tied anchors handled by engine.
        }
    }

    private static ImportResult Fail(OutcomeKind kind, Diagnostic d) =>
        new(kind, null, Array.Empty<CoreSegment>(), Array.Empty<AgeAnchor>(),
            Array.Empty<ReservoirCorrection>(), Array.Empty<EventLayer>(),
            Array.Empty<ExclusionDecision>(), new[] { d }, Array.Empty<Judgment>());
}
