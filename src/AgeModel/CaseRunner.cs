using System.Reflection;
using System.Text.Json;

namespace AgeModel;

/// <summary>离线案例包加载与重放：所有操作走真实 Service/Engine/Store，留下真实事件。</summary>
public static class CaseRunner
{
    public static string LoadCaseJson()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("core-age-scale.case.json"));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static Material ParseMaterial(JsonElement el)
    {
        Material m = el.GetProperty("kind").GetString() switch
        {
            "depth-datum" => new DepthDatum
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                Unit = Opt(el, "unit"), Datum = Opt(el, "datum"), CoreId = Req(el, "coreId"),
                DepthM = el.GetProperty("depthM").GetDouble(), DatumType = Req(el, "datumType")
            },
            "segment-gap" => new SegmentGap
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                Unit = Opt(el, "unit"), Datum = Opt(el, "datum"), CoreId = Req(el, "coreId"),
                FromDepthM = el.GetProperty("fromDepthM").GetDouble(), ToDepthM = el.GetProperty("toDepthM").GetDouble(),
                GapKind = Req(el, "gapKind"), Reason = Req(el, "reason")
            },
            "radio-age" => new RadioAge
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                Unit = Opt(el, "unit"), Datum = Opt(el, "datum"), CoreId = Req(el, "coreId"),
                DepthM = el.GetProperty("depthM").GetDouble(), Method = Req(el, "method"),
                SamplesKa = el.GetProperty("samplesKa").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                ReservoirOffsetId = Opt(el, "reservoirOffsetId")
            },
            "reservoir-offset" => new ReservoirOffset
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                Unit = Opt(el, "unit"), Datum = Opt(el, "datum"), CoreId = Req(el, "coreId"),
                OffsetYears = el.GetProperty("offsetYears").GetDouble(), AppliesTo = Req(el, "appliesTo")
            },
            "event-layer" => new EventLayer
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                Unit = Opt(el, "unit"), Datum = Opt(el, "datum"), CoreId = Req(el, "coreId"),
                DepthM = el.GetProperty("depthM").GetDouble(), EventName = Req(el, "eventName"),
                AgeKa = el.TryGetProperty("ageKa", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : null,
                Correlation = Req(el, "correlation")
            },
            "exclusion" => new ExclusionRecord
            {
                Id = Req(el, "id"), Version = el.GetProperty("version").GetInt32(), Raw = Req(el, "raw"),
                TargetId = Req(el, "targetId"), Reason = Req(el, "reason"), DecidedBy = Req(el, "decidedBy")
            },
            var k => throw new InvalidDataException($"unknown kind {k}")
        };
        return m;
    }

    private static string Req(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidDataException($"material missing required field '{name}' at {el.GetRawText()[..Math.Min(80, el.GetRawText().Length)]}");

    private static string? Opt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public sealed record ReplayStep(string Op, string Note, string Status, string Detail);

    /// <summary>按脚本重放案例：导入→冻结(并发)→参数修订→局部源改动→冲突决定→新锚点对比。</summary>
    public static List<ReplayStep> Replay(Service svc, string? caseJson = null)
    {
        var doc = JsonDocument.Parse(caseJson ?? LoadCaseJson());
        var root = doc.RootElement;
        var log = new List<ReplayStep>();
        var extras = root.GetProperty("extraMaterials");

        foreach (var opEl in root.GetProperty("ops").EnumerateArray())
        {
            var op = opEl.GetProperty("op").GetString()!;
            var note = opEl.TryGetProperty("note", out var n) ? n.GetString()! : "";
            switch (op)
            {
                case "import":
                {
                    var mats = root.GetProperty("materials").EnumerateArray().Select(ParseMaterial).ToList();
                    var r = svc.ImportMaterials(mats, root.GetProperty("caseId").GetString()!);
                    log.Add(new(op, note, r.Status.ToString(), $"imported={r.Value?.Count ?? 0}, diags={r.Diagnostics.Count}"));
                    break;
                }
                case "freeze":
                {
                    var r = svc.Freeze(opEl.GetProperty("modelId").GetString()!, opEl.GetProperty("by").GetString()!);
                    log.Add(new(op, note, r.Status.ToString(), opEl.GetProperty("modelId").GetString()!));
                    break;
                }
                case "param":
                {
                    var e = svc.AdjustParams(opEl.GetProperty("tolerance").GetDouble(), opEl.GetProperty("maxCandidates").GetInt32(), opEl.GetProperty("budget").GetInt32(), opEl.GetProperty("by").GetString()!);
                    log.Add(new(op, note, "Ok", $"paramVersion={svc.Params.Version} evt={e.Id}"));
                    break;
                }
                case "material":
                {
                    var m = ParseMaterial(extras.GetProperty(opEl.GetProperty("ref").GetString()!));
                    var r = svc.ImportMaterials(new() { m }, "case-revision");
                    log.Add(new(op, note, r.Status.ToString(), $"{m.Id} v{m.Version}"));
                    break;
                }
                case "decide":
                {
                    var e = svc.Decide(opEl.GetProperty("target").GetString()!, opEl.GetProperty("accept").GetBoolean(), opEl.GetProperty("reason").GetString()!, opEl.GetProperty("by").GetString()!);
                    log.Add(new(op, note, "Ok", $"evt={e.Id}"));
                    break;
                }
                case "propose":
                {
                    var m = (RadioAge)ParseMaterial(extras.GetProperty(opEl.GetProperty("ref").GetString()!));
                    var r = svc.ProposeAnchor(m);
                    log.Add(new(op, note, r.Status.ToString(), $"proposed={m.Id}"));
                    break;
                }
            }
        }
        return log;
    }
}
