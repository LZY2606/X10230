using System.Text.Json;
using AgeModel.Domain;

namespace AgeModel.Engine;

/// <summary>案例包：领域专用（岩芯年代尺），不是通用表格。脚本用于确定性重放。</summary>
public sealed record CasePack(
    SourceMaterials Materials,
    RuleVersion InitialRules,
    IReadOnlyList<RuleVersion> RuleVersions,
    IReadOnlyList<ScriptStep> Script,
    IReadOnlyList<Diagnostic> ParseDiagnostics);

public enum ScriptActionType { ParameterRevision, SourceChange, HumanDecision }

public sealed record ScriptStep(int Order, string Id, ScriptActionType Type, string Description,
    string? RuleVersionId, JsonElement? Payload);

public static class CasePackLoader
{
    private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);

    public static CasePack LoadFromFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(doc.RootElement);
    }

    public static CasePack Parse(JsonElement root)
    {
        var diags = new List<Diagnostic>();
        string path = "$";

        string sid = ReqString(root, "id", path, diags);
        string label = ReqString(root, "title", path, diags) ?? sid;

        var basisEl = root.GetProperty("basis");
        var basis = new AgeBasis(
            OptString(basisEl, "scale") ?? "cal BP",
            OptInt(basisEl, "calCurve"), OptInt(basisEl, "epoch"));

        var coreEl = root.GetProperty("core");
        string datum = OptString(coreEl, "datum") ?? "core top";
        var top = ParseDepth(coreEl.GetProperty("top"), datum, "$.core.top", diags);
        var bottom = ParseDepth(coreEl.GetProperty("bottom"), datum, "$.core.bottom", diags);

        var units = new Dictionary<string, string>();
        if (coreEl.TryGetProperty("units", out var um))
            foreach (var p in um.EnumerateObject()) units[p.Name] = p.Value.GetString() ?? "";

        var gaps = new List<SegmentGap>();
        if (coreEl.TryGetProperty("gaps", out var gapsEl))
            foreach (var g in gapsEl.EnumerateArray())
            {
                string gp = $"$.core.gaps[{g.GetRawText().Length}]";
                string gid = ReqString(g, "id", gp, diags) ?? $"gap-{gaps.Count + 1}";
                gp = $"$.core.gaps#{gid}";
                var gt = ParseDepth(g.GetProperty("top"), datum, gp + ".top", diags);
                var gb = ParseDepth(g.GetProperty("bottom"), datum, gp + ".bottom", diags);
                var kind = ParseEnum<GapKind>(g, "kind", gp, diags);
                Qty? hiatus = null;
                if (g.TryGetProperty("hiatusYears", out var h))
                    hiatus = ParseQty(h, "cal yr", gp + ".hiatusYears", diags);
                gaps.Add(new SegmentGap(gid, gt, gb, kind, OptString(g, "note"), hiatus));
            }

        var reservoirs = new List<ReservoirOffset>();
        if (root.TryGetProperty("reservoirs", out var resEl))
            foreach (var r in resEl.EnumerateArray())
            {
                string rp = $"$.reservoirs#{OptString(r, "id")}";
                var delta = ParseQty(r.GetProperty("deltaR"), "14C yr", rp + ".deltaR", diags);
                var dist = ParseDistribution(r.GetProperty("distribution"), rp + ".distribution", diags);
                reservoirs.Add(new ReservoirOffset(
                    ReqString(r, "id", rp, diags) ?? $"res-{reservoirs.Count + 1}",
                    delta, dist, OptString(r, "source") ?? "", OptString(r, "appliesTo")));
            }

        var events = new List<EventBed>();
        if (root.TryGetProperty("eventBeds", out var evEl))
            foreach (var e in evEl.EnumerateArray())
            {
                string ep = $"$.eventBeds#{OptString(e, "id")}";
                var at = ParseDepth(e.GetProperty("at"), datum, ep + ".at", diags);
                AgeDistribution? d = e.TryGetProperty("age", out var ae)
                    ? ParseDistribution(ae, ep + ".age", diags) : null;
                events.Add(new EventBed(
                    ReqString(e, "id", ep, diags) ?? $"ev-{events.Count + 1}", at,
                    OptString(e, "name") ?? "", OptString(e, "correlationKey"), d,
                    e.TryGetProperty("correlationWeight", out var cw) && cw.TryGetDouble(out var cwv) ? cwv : null));
            }

        var anchors = new List<AgeAnchor>();
        foreach (var a in root.GetProperty("anchors").EnumerateArray())
        {
            string ap = $"$.anchors#{OptString(a, "id")}";
            string aid = ReqString(a, "id", ap, diags) ?? $"anc-{anchors.Count + 1}";
            ap = $"$.anchors#{aid}";
            var at = ParseDepth(a.GetProperty("at"), datum, ap + ".at", diags);
            var kind = ParseEnum<AgeKind>(a, "kind", ap, diags);
            var dist = ParseDistribution(a.GetProperty("age"), ap + ".age", diags);
            Exclusion? ex = null;
            if (a.TryGetProperty("exclusion", out var xe))
                ex = new Exclusion(
                    OptString(xe, "reason") ?? "未给理由",
                    OptString(xe, "decidedBy") ?? "unknown",
                    OptString(xe, "decidedAt") ?? "unknown",
                    OptString(xe, "evidenceRef"));
            anchors.Add(new AgeAnchor
            {
                Id = aid, At = at, Kind = kind, Distribution = dist,
                EventBedId = OptString(a, "eventBedId"),
                ReservoirId = OptString(a, "reservoirId"),
                LabCode = OptString(a, "labCode"),
                Exclusion = ex,
                Note = OptString(a, "note")
            });
        }

        var core = new CoreProfile(sid + "-core", OptString(coreEl, "name") ?? label, top, bottom, gaps, units);
        var mats = new SourceMaterials
        {
            SourceId = sid, Label = label, Core = core,
            Anchors = anchors, EventBeds = events, Reservoirs = reservoirs, Basis = basis
        };

        var rules = new List<RuleVersion>();
        RuleVersion initial;
        if (root.TryGetProperty("ruleVersions", out var rvEl))
        {
            foreach (var v in rvEl.EnumerateArray())
                rules.Add(ParseRule(v));
            initial = rules.First(r => r.Number == 1);
        }
        else initial = new RuleVersion("rules-v1", 1, "default", "初始规则", 0.95, 256, 2000, 80);

        var script = new List<ScriptStep>();
        if (root.TryGetProperty("script", out var scEl))
        {
            int order = 0;
            foreach (var s in scEl.EnumerateArray())
            {
                string sp = $"$.script[{order}]";
                var type = ParseEnum<ScriptActionType>(s, "action", sp, diags);
                script.Add(new ScriptStep(order,
                    OptString(s, "id") ?? $"step-{order}", type,
                    OptString(s, "description") ?? "",
                    OptString(s, "ruleVersionId"),
                    s.TryGetProperty("payload", out var pl) ? pl : null));
                order++;
            }
        }

        return new CasePack(mats, initial, rules.Count == 0 ? [initial] : rules, script, diags);
    }

    private static RuleVersion ParseRule(JsonElement v) => new(
        OptString(v, "id") ?? $"rules-v{OptInt(v, "number") ?? 1}",
        OptInt(v, "number") ?? 1,
        OptString(v, "name") ?? "",
        OptString(v, "notes") ?? "",
        v.TryGetProperty("monotonicityProbabilityThreshold", out var mt) && mt.TryGetDouble(out var m) ? m : 0.95,
        v.TryGetProperty("candidateBudget", out var cb) && cb.TryGetInt32(out var c) ? c : 256,
        v.TryGetProperty("monteCarloDraws", out var mc) && mc.TryGetInt32(out var d) ? d : 2000,
        v.TryGetProperty("rateGridPoints", out var rg) && rg.TryGetInt32(out var g) ? g : 80);

    private static Depth ParseDepth(JsonElement el, string datum, string p, List<Diagnostic> diags)
    {
        string? raw = null; double? val = null; string unit = "cm";
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("raw", out var rw)) raw = rw.GetString();
            if (el.TryGetProperty("unit", out var un)) unit = un.GetString() ?? "cm";
            if (el.TryGetProperty("value", out var v)) val = TryDouble(v, p, diags);
            string? localDatum = el.TryGetProperty("datum", out var dt) ? dt.GetString() : null;
            if (localDatum is not null && localDatum != datum)
                diags.Add(new Diagnostic("DATUM-MISMATCH", Severity.Warning, p,
                    $"深度基准 {localDatum} 与岩芯基准 {datum} 不一致，已保留原始表示，未强制换算。"));
            if (val is null && raw is not null)
                diags.Add(new Diagnostic("UNCONVERTIBLE-DEPTH", Severity.Error, p,
                    $"深度值 '{raw}' 无法规范化为数值，保留原位置。"));
            return new Depth(val ?? double.NaN, localDatum ?? datum, raw ?? el.GetRawText());
        }
        val = TryDouble(el, p, diags);
        return new Depth(val ?? double.NaN, datum, el.GetRawText());
    }

    private static Qty ParseQty(JsonElement el, string defaultUnit, string p, List<Diagnostic> diags)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            string? raw = el.TryGetProperty("raw", out var rw) ? rw.GetString() : null;
            string unit = el.TryGetProperty("unit", out var un) ? un.GetString() ?? defaultUnit : defaultUnit;
            double? val = el.TryGetProperty("value", out var v) ? TryDouble(v, p, diags) : null;
            if (val is null)
                return new Qty(null, unit, raw ?? el.GetRawText(), "无法解析数值");
            if (unit != defaultUnit && !KnownConversions.ContainsKey((unit, defaultUnit)))
                return new Qty(null, unit, raw ?? el.GetRawText(), $"无 {unit}→{defaultUnit} 转换规则");
            double converted = unit == defaultUnit ? val.Value : val.Value * KnownConversions[(unit, defaultUnit)];
            return new Qty(converted, defaultUnit, raw ?? el.GetRawText());
        }
        return new Qty(TryDouble(el, p, diags), defaultUnit, el.GetRawText());
    }

    private static readonly Dictionary<(string, string), double> KnownConversions = new()
    {
        { ("m", "cm"), 100 }, { ("cm", "m"), 0.01 },
        { ("ka BP", "cal BP"), 1000 }, { ("14C yr", "cal yr"), 1.0 }, { ("14C yr BP", "cal BP"), 1.0 }
    };

    private static AgeDistribution ParseDistribution(JsonElement el, string p, List<Diagnostic> diags)
    {
        string type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "normal" : "normal";
        switch (type)
        {
            case "normal":
                return new NormalDist(Num(el, "mu", p, diags), Num(el, "sigma", p, diags));
            case "lognormal":
                return new LogNormalDist(Num(el, "logMu", p, diags), Num(el, "logSigma", p, diags));
            case "t":
                return new StudentTails(Num(el, "location", p, diags), Num(el, "scale", p, diags),
                    Num(el, "df", p, diags));
            case "samples":
                var samples = new List<double>();
                foreach (var s in el.GetProperty("samples").EnumerateArray())
                {
                    double? v = TryDouble(s, p, diags);
                    if (v is not null) samples.Add(v.Value);
                }
                return new SampleDist(samples);
            case "unconverted":
                diags.Add(new Diagnostic("UNCONVERTIBLE-AGE", Severity.Error, p,
                    $"年龄 '{OptString(el, "raw")}' 无法转换（{OptString(el, "reason")}）；保留原位置，不参与画图。"));
                return new UnconvertedDist(OptString(el, "raw") ?? el.GetRawText(),
                    OptString(el, "reason") ?? "无转换路径");
            default:
                diags.Add(new Diagnostic("UNKNOWN-DIST", Severity.Error, p, $"未知分布类型 {type}"));
                return new UnconvertedDist(el.GetRawText(), $"未知分布类型 {type}");
        }
    }

    private static double Num(JsonElement e, string name, string p, List<Diagnostic> d)
    {
        if (e.TryGetProperty(name, out var v) && v.TryGetDouble(out var x)) return x;
        d.Add(new Diagnostic("MISSING-NUM", Severity.Error, $"{p}.{name}", $"缺少数值字段 {name}"));
        return double.NaN;
    }

    private static double? TryDouble(JsonElement e, string p, List<Diagnostic> d)
    {
        if (e.TryGetDouble(out var x) && double.IsFinite(x)) return x;
        return null;
    }

    private static string? ReqString(JsonElement e, string name, string p, List<Diagnostic> d)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        d.Add(new Diagnostic("MISSING-FIELD", Severity.Error, $"{p}.{name}", $"缺少必填字段 {name}"));
        return null;
    }

    private static string? OptString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? OptInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static TEnum ParseEnum<TEnum>(JsonElement e, string name, string p, List<Diagnostic> d)
        where TEnum : struct
    {
        string? s = OptString(e, name);
        if (s is not null && Enum.TryParse<TEnum>(s, true, out var parsed)) return parsed;
        d.Add(new Diagnostic("BAD-ENUM", Severity.Error, $"{p}.{name}", $"字段 {name}='{s}' 无法解析为 {typeof(TEnum).Name}"));
        return default;
    }
}
