using System.Globalization;
using ChronoCore.Domain.Model;

namespace ChronoCore.Domain.Ingest;

/// <summary>Builds the immutable CorePackage from lexed source lines.
/// Original tokens are stored everywhere; conversion to canonical units
/// happens later. Unconvertible values stay in place with diagnostics.</summary>
public static class PackageReader
{
    public sealed record ReadResult(CorePackage? Package, IReadOnlyList<Diagnostic> Diagnostics, bool BudgetHit);

    private static readonly string[] DepthUnits = { "cm", "m", "mm" };
    private static readonly string[] AgeUnits = { "cal yr BP", "yr BP", "a BP", "ka BP", "cal BP", "yr" };

    public static ReadResult Read(string sourceId, string text, ParseBudget? budget = null)
    {
        budget ??= new ParseBudget(sourceId);
        var lex = LineSyntax.Lex(text, budget);
        var diags = lex.Diagnostics.ToList();
        if (lex.BudgetHit) return new ReadResult(null, diags, true);

        var rawLines = text.Replace("\r\n", "\n").Split('\n')
            .Select((t, i) => new RawSourceLine(i + 1, t, sourceId)).ToList();

        string? caseId = null, caseVersion = null;
        DepthDatum? datum = null;
        var segments = new List<CoreSegment>();
        var boundaries = new List<Boundary>();
        var anchors = new List<AgeAnchor>();
        var events = new List<EventLayer>();
        var exclusions = new List<Exclusion>();

        foreach (var line in lex.Lines)
        {
            try
            {
                switch (line.Keyword)
                {
                    case "CASE":
                        caseId = Req(line, "id"); caseVersion = line.Value("version") ?? "0";
                        break;
                    case "DATUM":
                        datum = ReadDatum(line, diags);
                        break;
                    case "SEGMENT":
                        segments.Add(ReadSegment(line, diags));
                        break;
                    case "BOUNDARY":
                        boundaries.Add(ReadBoundary(line, diags));
                        break;
                    case "ANCHOR":
                        anchors.Add(ReadAnchor(line, diags));
                        break;
                    case "EVENT":
                        events.Add(ReadEvent(line, diags));
                        break;
                    case "EXCLUDE":
                        exclusions.Add(ReadExclusion(line));
                        break;
                    default:
                        diags.Add(Err(line, "SYNTAX_KEYWORD", $"未知记录类型“{line.Keyword}”"));
                        break;
                }
            }
            catch (FormatException fex)
            {
                diags.Add(Err(line, "FIELD_FORMAT", fex.Message));
            }
        }

        if (caseId is null)
            diags.Add(new Diagnostic(DiagSeverity.CorruptFormat, "CASE_MISSING",
                "缺少 CASE 声明（id/version）", sourceId, 0, 1));
        if (datum is null)
            diags.Add(new Diagnostic(DiagSeverity.CorruptFormat, "DATUM_MISSING",
                "缺少 DATUM 深度基准声明", sourceId, 0, 1));

        if (diags.Any(d => d.Severity == DiagSeverity.CorruptFormat))
            return new ReadResult(null, diags, false);

        var pkg = new CorePackage(caseId!, caseVersion ?? "0", datum!,
            OrderSegments(segments, diags), boundaries, anchors, events, exclusions,
            rawLines, diags);
        return new ReadResult(pkg, diags, false);
    }

    private static Diagnostic Err(SourceLine l, string code, string msg) =>
        new(DiagSeverity.CorruptFormat, code, msg, "", l.Number, 1);

    private static string Req(SourceLine l, string name) =>
        l.Value(name) ?? throw new FormatException($"第 {l.Number} 行缺少必填字段 {name}");

    private static DepthDatum ReadDatum(SourceLine l, List<Diagnostic> diags)
    {
        var id = Req(l, "id");
        var name = l.Value("name") ?? id;
        var axis = Req(l, "axis");
        if (!DepthUnits.Contains(axis))
            diags.Add(new Diagnostic(DiagSeverity.Unconverted, "UNIT_UNKNOWN",
                $"深度轴单位“{axis}”无法规范化，原值保留", "", l.Number, Column(l, "axis")));
        var dirText = (l.Value("direction") ?? "down").ToLowerInvariant();
        var dir = dirText switch
        {
            "down" or "downward" => DepthDirection.Downward,
            "up" or "upward" => DepthDirection.Upward,
            _ => throw new FormatException($"direction 必须为 down/up，收到“{dirText}”")
        };
        var depthTok = Req(l, "depth");
        var m = Measure.Parse(depthTok, "基准面深度");
        if (m.Unit != axis)
            diags.Add(new Diagnostic(DiagSeverity.Unconverted, "UNIT_MISMATCH",
                $"DATUM 深度单位 {m.Unit} 与轴单位 {axis} 不一致，原值保留", "", l.Number, Column(l, "depth")));
        return new DepthDatum(id, name, axis, dir, m.Value, depthTok, diags.ToList());
    }

    private static int Column(SourceLine l, string field) =>
        l.Fields.FirstOrDefault(f => f.Name == field)?.Column ?? 1;

    private static DepthRef Depth(string token, string datumId)
    {
        var m = Measure.Parse(token, "深度");
        return new DepthRef(m.Value, m.Unit, datumId, token);
    }

    private static IReadOnlyList<CoreSegment> OrderSegments(List<CoreSegment> raw, List<Diagnostic> diags)
    {
        var ordered = raw.OrderBy(s => s.Sequence).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seq = 0;
        foreach (var s in ordered)
        {
            if (s.Sequence != seq)
                diags.Add(new Diagnostic(DiagSeverity.Warning, "SEGMENT_SEQ_GAP",
                    $"分段序号不连续：{s.Id} 声明 seq={s.Sequence}，相邻序号={seq}", "", 0, 0));
            seq++;
        }
        return ordered;
    }

    private static CoreSegment ReadSegment(SourceLine l, List<Diagnostic> diags)
    {
        var id = Req(l, "id");
        var seq = int.Parse(Req(l, "seq"), CultureInfo.InvariantCulture);
        var datumHint = l.Value("datum") ?? "";
        var top = Depth(Req(l, "top"), datumHint);
        var bottom = Depth(Req(l, "bottom"), datumHint);
        var local = new List<Diagnostic>();
        if (!DepthUnits.Contains(top.Unit) || !DepthUnits.Contains(bottom.Unit))
            local.Add(new Diagnostic(DiagSeverity.Unconverted, "UNIT_UNKNOWN",
                $"分段 {id} 深度单位无法规范化，原值保留", "", l.Number, Column(l, "top")));
        return new CoreSegment(id, seq, top, bottom, l.Value("lith") ?? "", l.Raw, local);
    }

    private static Boundary ReadBoundary(SourceLine l, List<Diagnostic> diags)
    {
        var id = Req(l, "id");
        var kind = (Req(l, "kind")).ToLowerInvariant() switch
        {
            "gap" => BoundaryKind.SegmentGap,
            "unconformity" or "unconf" => BoundaryKind.Unconformity,
            "break" => BoundaryKind.CoreBreak,
            _ => throw new FormatException($"BOUNDARY kind 必须为 gap/unconformity/break，收到于 {id}")
        };
        var datumHint = l.Value("datum") ?? "";
        var at = Depth(Req(l, "at"), datumHint);
        double? missing = null;
        var missTok = l.Value("missing");
        var local = new List<Diagnostic>();
        if (missTok is not null)
        {
            try { var mm = Measure.Parse(missTok, "缺失厚度"); missing = mm.Value; }
            catch (FormatException fex)
            {
                local.Add(new Diagnostic(DiagSeverity.Unconverted, "UNIT_UNKNOWN",
                    fex.Message + "；原值保留", "", l.Number, Column(l, "missing")));
            }
        }
        return new Boundary(id, kind, at, missing, l.Raw, local);
    }

    private static AgeAnchor ReadAnchor(SourceLine l, List<Diagnostic> diags)
    {
        var id = Req(l, "id");
        var label = l.Value("label") ?? id;
        var at = Depth(Req(l, "at"), l.Value("datum") ?? "");
        var ageTok = Req(l, "age");
        var ageM = Measure.Parse(ageTok, "年龄");
        var ageUnit = ageM.Unit;
        var local = new List<Diagnostic>();
        if (!AgeUnits.Contains(ageUnit))
            local.Add(new Diagnostic(DiagSeverity.Unconverted, "UNIT_UNKNOWN",
                $"年龄单位“{ageUnit}”无法规范化，原值保留", "", l.Number, Column(l, "age")));

        var kind = DistributionKind.Gaussian;
        double sy = 0, so = 0;
        var quants = new List<Quantile>();
        var dist = l.Value("dist");
        if (dist is not null)
            kind = dist.ToLowerInvariant() switch
            {
                "gauss" or "gaussian" => DistributionKind.Gaussian,
                "asym" or "asymmetric" => DistributionKind.AsymmetricGaussian,
                "empirical" or "quantiles" or "q" => DistributionKind.EmpiricalQuantiles,
                "uniform" => DistributionKind.Uniform,
                _ => throw new FormatException($"未知分布类型 {dist}（锚点 {id}）")
            };

        var sigmaTok = l.Value("sigma");
        if (sigmaTok is not null)
        {
            var sm = Measure.Parse(sigmaTok, "sigma");
            sy = so = sm.Value;
        }
        var syTok = l.Value("sigmaYoung");
        var soTok = l.Value("sigmaOld");
        if (syTok is not null) sy = Measure.Parse(syTok, "sigmaYoung").Value;
        if (soTok is not null) so = Measure.Parse(soTok!, "sigmaOld").Value;
        if (kind == DistributionKind.AsymmetricGaussian && (syTok is null || soTok is null))
            throw new FormatException($"非对称分布锚点 {id} 必须同时给出 sigmaYoung 与 sigmaOld");

        foreach (var f in l.Fields.Where(f => f.Name == "q"))
        {
            try { quants.Add(Quantile.Parse(f.RawValue)); }
            catch (FormatException fex)
            {
                throw new FormatException($"锚点 {id}：{fex.Message}");
            }
        }
        if (kind == DistributionKind.EmpiricalQuantiles && quants.Count < 2)
            throw new FormatException($"经验分布锚点 {id} 至少需要两个 q=p=value 分位数");

        if (quants.Count == 0)
        {
            // Derived quantile envelope; central value is still retained separately,
            // the distribution is never reduced to it.
            quants = kind switch
            {
                DistributionKind.Gaussian => new()
                {
                    new(0.025, ageM.Value - 1.96 * sy), new(0.5, ageM.Value),
                    new(0.975, ageM.Value + 1.96 * sy)
                },
                DistributionKind.AsymmetricGaussian => new()
                {
                    new(0.025, ageM.Value - 1.96 * sy), new(0.5, ageM.Value),
                    new(0.975, ageM.Value + 1.96 * so)
                },
                DistributionKind.Uniform => new()
                {
                    new(0.025, ageM.Value - sy), new(0.5, (2 * ageM.Value - sy + so) / 2.0),
                    new(0.975, ageM.Value + so)
                },
                _ => quants
            };
        }

        var scale = l.Value("scale") ?? "cal";
        var basis = new TimeBasis(scale, l.Value("calendar") ?? "BP", l.Raw);
        var resTok = l.Value("reservoir") ?? "0";
        double reservoir = 0;
        try { reservoir = Measure.Parse(resTok, "储层偏移").Value; }
        catch (FormatException fex)
        {
            local.Add(new Diagnostic(DiagSeverity.Unconverted, "RESERVOIR_UNPARSED",
                fex.Message + "；原值保留，偏移视为 0 仅用于派生层", "", l.Number, Column(l, "reservoir")));
        }

        var dist2 = new AgeDistribution(id, kind, ageM.Value, sy, so, ageUnit, basis,
            reservoir, resTok, quants, ageTok);
        if (dist2.HasLongTail)
            local.Add(new Diagnostic(DiagSeverity.Warning, "DIST_LONG_TAIL",
                $"锚点 {id} 检出年龄分布长尾，按分位数传播，不得用均值替代", "", l.Number, Column(l, "age")));

        var method = l.Value("method") ?? "unspecified";
        return new AgeAnchor(id, label, at, dist2, method, l.Value("material") ?? id, l.Number, local);
    }

    private static EventLayer ReadEvent(SourceLine l, List<Diagnostic> diags)
    {
        var id = Req(l, "id");
        var at = Depth(Req(l, "at"), l.Value("datum") ?? "");
        double? thick = null;
        var thickTok = l.Value("thick");
        if (thickTok is not null)
        {
            try { thick = Measure.Parse(thickTok, "厚度").Value; }
            catch (FormatException) { /* leave null, raw preserved */ }
        }
        double? tied = null;
        var ageTok = l.Value("age");
        if (ageTok is not null)
        {
            try { tied = Measure.Parse(ageTok, "事件年龄").Value; }
            catch (FormatException) { }
        }
        return new EventLayer(id, l.Value("corr") ?? id, at, thick,
            l.Value("kind") ?? "event", tied, l.Value("basis") ?? "", l.Raw, Array.Empty<Diagnostic>());
    }

    private static Exclusion ReadExclusion(SourceLine l) => new(
        Req(l, "id"), Req(l, "target"), Req(l, "reason"),
        l.Value("note") ?? "", l.Number, l.Raw);
}
