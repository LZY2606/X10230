using System.Text.Json;
using Chrono.CasePack;
using Chrono.Domain;

namespace Chrono.Engine;

public sealed record ImportReport(
    bool Ok, string Outcome, List<Diagnostic> Diagnostics, List<string> MaterialIds, string? PackageId);

/// <summary>
/// 案例包导入：所有领域量保留原始表示，规范化只发生在派生字段。
/// 三种结果严格区分：FormatCorrupt（结构损坏）、SemanticImpasse（语义无解）、Accepted（诊断可含 warning）。
/// </summary>
public sealed class CaseImporter
{
    public static CasePack ParsePackage(string json)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<CasePack>(json, Canonical.Json);
            if (pack is null) throw new JsonException("根对象为空");
            return pack;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new CaseFormatException($"案例包格式损坏：{ex.Message}");
        }
    }

    public ImportReport Apply(MaterialState state, CasePack pack, string rawSourceRef = "pack")
    {
        var diags = new List<Diagnostic>();
        var ids = new List<string>();
        var semanticErrors = new List<Diagnostic>();

        state.Datum = new DepthDatum(pack.DepthDatum.Id, pack.DepthDatum.Name,
            pack.DepthDatum.VerticalDatum, pack.DepthDatum.PositiveDirection, pack.DepthDatum.Note);
        ids.Add(pack.DepthDatum.Id);

        foreach (var s in pack.Sections)
        {
            var top = ParseLength(s.TopRaw, s.DepthUnit, s.Id, "topRaw", diags);
            var @base = ParseLength(s.BaseRaw, s.DepthUnit, s.Id, "baseRaw", diags);
            if (top.Value.HasValue && @base.Value.HasValue && @base.Value.Value <= top.Value.Value)
                semanticErrors.Add(Diagnostic.Error("SECTION_INVERTED",
                    $"分段 {s.Code} 顶深不小于底深", s.Id, "baseRaw", rawSourceRef));
            var sec = new CoreSection(s.Id, s.Code, top, @base, s.Recovery,
                new[] { new SourceCitation(s.Id, "section", s.Source, rawSourceRef) });
            state.Sections[s.Id] = sec;
            ids.Add(s.Id);
        }

        foreach (var b in pack.Boundaries)
        {
            if (!Enum.TryParse<BoundaryKind>(b.Kind, true, out var kind))
            {
                semanticErrors.Add(Diagnostic.Error("BOUNDARY_KIND_UNKNOWN",
                    $"未知边界类型 {b.Kind}", b.Id, "kind", rawSourceRef));
                continue;
            }
            var depth = ParseLength(b.DepthRaw, "mbsf", b.Id, "depthRaw", diags);
            QValue? mt = b.MissingTopRaw is null ? null : ParseLength(b.MissingTopRaw, "mbsf", b.Id, "missingTopRaw", diags);
            QValue? mb = b.MissingBaseRaw is null ? null : ParseLength(b.MissingBaseRaw, "mbsf", b.Id, "missingBaseRaw", diags);
            state.Boundaries[b.Id] = new SegmentBoundary(b.Id, kind, depth,
                b.BetweenSectionTop, b.BetweenSectionBase, mt, mb, b.Rationale,
                new[] { new SourceCitation(b.Id, "boundary", b.Source, rawSourceRef) });
            ids.Add(b.Id);
        }

        foreach (var r in pack.Reservoirs)
        {
            var off = ParseAgeYears(r.OffsetRaw, r.Unit, r.Id, "offsetRaw", diags);
            var sig = ParseAgeYears(r.SigmaRaw, r.Unit, r.Id, "sigmaRaw", diags);
            state.Reservoirs[r.Id] = new ReservoirCorrection(r.Id, r.Name, off, sig,
                r.MarineCurve, pack.Parameters.Version, r.Note);
            ids.Add(r.Id);
        }

        foreach (var c in pack.Curves)
        {
            var pts = c.Points.Select(p => new CalPoint(p[0], p[1])).ToList();
            state.Curves[c.Id] = new CalibrationCurve(c.Id, c.Name, pts);
            ids.Add(c.Id);
        }

        foreach (var e in pack.EventBeds)
        {
            var d = ParseLength(e.DepthRaw, e.DepthUnit, e.Id, "depthRaw", diags);
            state.EventBeds[e.Id] = new EventBed(e.Id, e.Name, e.EventType, d,
                e.CorrelatedGlobalEvent, e.Confidence, e.Note);
            ids.Add(e.Id);
        }

        foreach (var a in pack.Anchors)
        {
            var depth = ParseLength(a.DepthRaw, a.DepthUnit, a.Id, "depthRaw", diags);
            AgeDistribution? dist = null;
            if (a.Distribution is { } el)
            {
                try { dist = el.Deserialize<AgeDistribution>(Canonical.Json); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    throw new CaseFormatException($"锚点 {a.Id} 分布格式损坏：{ex.Message}");
                }
            }
            else if (a.ConventionalBp.HasValue)
            {
                if (string.IsNullOrEmpty(a.ReservoirId) || !pack.Reservoirs.Any(r => r.Id == a.ReservoirId))
                    semanticErrors.Add(Diagnostic.Error("RESERVOIR_MISSING",
                        $"放射性碳锚点 {a.Id} 引用的储层偏移缺失", a.Id, "reservoirId", rawSourceRef));
                var curveId = pack.Reservoirs.FirstOrDefault(r => r.Id == a.ReservoirId)?.MarineCurve ?? "MARINE23-FIC";
                dist = new RadiocarbonCalDist(a.Id, a.ConventionalBp.Value, a.OneSigmaBp ?? 0,
                    a.ReservoirId ?? "", curveId, pack.Parameters.Version);
            }
            var anchor = new AgeAnchor(a.Id, a.Label, Enum.Parse<AnchorKind>(a.Kind, true), depth,
                a.AgeText, a.AgeUnit, a.ConventionalBp, a.OneSigmaBp, dist,
                a.ReservoirId, a.EventId, a.InvertedFlag, a.TieGroup,
                new[] { new SourceCitation(a.Id, "anchor", a.Source, rawSourceRef) });
            state.Anchors[a.Id] = anchor;
            state.AnchorSourceVersion[a.Id] = "src/1";
            ids.Add(a.Id);

            if (a.EventId is not null && !state.EventBeds.ContainsKey(a.EventId) && !pack.EventBeds.Any(e => e.Id == a.EventId))
                semanticErrors.Add(Diagnostic.Error("EVENT_REF_BROKEN",
                    $"锚点 {a.Id} 引用的事件层 {a.EventId} 不存在", a.Id, "eventId", rawSourceRef));
        }

        foreach (var x in pack.Exclusions)
        {
            if (!state.Anchors.ContainsKey(x.AnchorId) && !pack.Anchors.Any(a => a.Id == x.AnchorId))
                semanticErrors.Add(Diagnostic.Error("EXCLUSION_REF_BROKEN",
                    $"排除决定引用的锚点 {x.AnchorId} 不存在", x.AnchorId, "exclusion", rawSourceRef));
            state.Exclusions[x.AnchorId] = new ExclusionDecision(
                $"dec:{x.AnchorId}", x.AnchorId, x.Reason, x.DecidedBy, x.DecidedAt, x.Excluded, null);
            ids.Add($"dec:{x.AnchorId}");
        }

        state.Params = new ModelParams(pack.Parameters.Version, pack.Parameters.SamplesPerAnchor,
            pack.Parameters.MaxCandidates, pack.Parameters.TailThresholdSigma,
            pack.Parameters.InversionRequiresDecision, pack.Parameters.DuplicateDepthToleranceMetres);

        var all = diags.Concat(semanticErrors).ToList();
        var outcome = semanticErrors.Count > 0
            ? nameof(CommandOutcome.SemanticImpasse)
            : nameof(CommandOutcome.Accepted);
        return new ImportReport(semanticErrors.Count == 0, outcome, all, ids, pack.PackageId);
    }

    public static QValue ParseLength(string raw, string unitText, string materialId, string field, List<Diagnostic> sink)
    {
        var num = Numbers.TryParse(raw);
        if (!LengthUnits.TryParse(unitText, out var unit))
        {
            sink.Add(Diagnostic.Warn("UNIT_UNKNOWN",
                $"未知长度单位 '{unitText}'，原始值 {raw} 保留原位、不参与作图", materialId, field));
            return QValue.Fail(raw, $"unknown length unit: {unitText}", materialId, unitText);
        }
        if (num is null)
        {
            sink.Add(Diagnostic.Warn("VALUE_UNPARSEABLE",
                $"深度值 '{raw}' 无法转换，保留原位并附诊断", materialId, field));
            return QValue.Fail(raw, $"unparseable length in {unitText}", materialId, unitText);
        }
        return QValue.Ok(LengthUnits.ToMetres(num.Value, unit), unitText, raw, materialId);
    }

    public static QValue ParseAgeYears(string raw, string unitText, string materialId, string field, List<Diagnostic> sink)
    {
        var num = Numbers.TryParse(raw);
        if (num is null)
        {
            sink.Add(Diagnostic.Warn("VALUE_UNPARSEABLE", $"偏移量 '{raw}' 无法转换", materialId, field));
            return QValue.Fail(raw, "unparseable age offset", materialId, unitText);
        }
        if (AgeUnits.TryParse(unitText, out _) || unitText.Trim().Equals("yr", StringComparison.OrdinalIgnoreCase))
            return QValue.Ok(num.Value, unitText, raw, materialId);
        sink.Add(Diagnostic.Warn("UNIT_UNKNOWN", $"未知时间单位 '{unitText}'，原值保留", materialId, field));
        return QValue.Fail(raw, $"unknown age unit: {unitText}", materialId, unitText);
    }
}

public sealed class CaseFormatException : Exception
{
    public CaseFormatException(string message) : base(message) { }
}
