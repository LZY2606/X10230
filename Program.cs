using System.Text.Json;
using DepthAge.Core;
using DepthAge.Server;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Environment.GetEnvironmentVariable("DEPTHAGE_DB")
    ?? Path.Combine(app.Environment.ContentRootPath, "depthage.db");
var casePath = Path.Combine(app.Environment.ContentRootPath, "cases", "core-chronoscale", "case.json");
var holder = new StoreHolder(dbPath);

lock (holder.Sync)
{
    if (holder.Store.Events().Count == 0 && File.Exists(casePath))
        CaseRunner.Run(CasePack.Load(File.ReadAllText(casePath)), holder.Store);
}

app.UseDefaultFiles();
app.UseStaticFiles();

// 当前（或指定历史事件时点的）完整投影状态
app.MapGet("/api/state", (long? @event) =>
{
    lock (holder.Sync) return Results.Json(Projector.Project(holder.Store, @event), JsonOptions.Pretty);
});

// 服务端 SVG 复核图：岩芯纵剖 + 锚点分布 + 速率区间 + 不确定度带（+ 冻结快照对比）
app.MapGet("/api/figure.svg", (long? @event, string? branch, bool compare) =>
{
    lock (holder.Sync)
    {
        var state = Projector.Project(holder.Store, @event);
        var svg = SvgFigure.Render(state, branch ?? DecisionPayload.MainBranch, compare);
        return Results.Content(svg, "image/svg+xml");
    }
});

// 反例档案：离线案例包的独立重放报告（基线 → 单源改动 → 边界组合 → 冲突决定）
app.MapGet("/api/archive", () =>
{
    var tmp = Path.Combine(Path.GetTempPath(), "depthage-archive-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
        using var store = new DepthAgeStore(tmp);
        var report = CaseRunner.Run(CasePack.Load(File.ReadAllText(casePath)), store);
        return Results.Json(report, JsonOptions.Pretty);
    }
    finally { try { File.Delete(tmp); } catch { } }
});

// 新增锚点（不可变材料 + 事件 + 局部重算）；输入不完整时返回带位置的诊断
app.MapPost("/api/anchor", async (HttpRequest req) =>
{
    JsonDocument body;
    try { body = await JsonDocument.ParseAsync(req.Body); }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { diagnostics = new[] { new Diagnostic("corrupt-format", Diagnostic.Error, ex.Message, null, "$") } });
    }
    var diags = new List<Diagnostic>();
    var root = body.RootElement;
    string? anchorRef = null;
    Quantity? depth = null;
    AgeDistribution? age = null;
    Quantity? offset = null;
    string? material = null, method = null;

    if (root.TryGetProperty("ref", out var r) && !string.IsNullOrWhiteSpace(r.GetString()))
        anchorRef = r.GetString()!.Trim();
    else
        diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "anchor ref is required", null, "$.ref"));

    if (root.TryGetProperty("depth", out var d) && d.TryGetProperty("value", out var dv) && dv.TryGetDouble(out var dval))
        depth = new Quantity(dval, d.TryGetProperty("unit", out var du) ? du.GetString() ?? "m" : "m",
            d.TryGetProperty("datum", out var dd) ? dd.GetString() : null);
    else
        diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "depth.value (number) is required", anchorRef, "$.depth.value"));

    if (root.TryGetProperty("age", out var a) && a.TryGetProperty("kind", out var ak))
    {
        var kind = ak.GetString() ?? "";
        var unit = a.TryGetProperty("unit", out var au) ? au.GetString() ?? "ka" : "ka";
        double? mean = a.TryGetProperty("mean", out var am) && am.TryGetDouble(out var mv) ? mv : null;
        double? sigma = a.TryGetProperty("sigma", out var asg) && asg.TryGetDouble(out var sv) ? sv : null;
        double? min = a.TryGetProperty("min", out var amin) && amin.TryGetDouble(out var mnv) ? mnv : null;
        double? max = a.TryGetProperty("max", out var amax) && amax.TryGetDouble(out var mxv) ? mxv : null;
        double[]? samples = null;
        if (a.TryGetProperty("samples", out var asp) && asp.ValueKind == JsonValueKind.Array)
            samples = asp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray();
        switch (kind)
        {
            case "normal" when mean is null || sigma is null or <= 0:
                diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "normal age requires mean and sigma>0", anchorRef, "$.age.mean")); break;
            case "uniform" when min is null || max is null || min >= max:
                diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "uniform age requires min<max", anchorRef, "$.age.min")); break;
            case "samples" when samples is null || samples.Length < 3:
                diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "samples age requires >=3 sample values", anchorRef, "$.age.samples")); break;
            case "normal" or "uniform" or "samples":
                age = new AgeDistribution(kind, unit, mean, sigma, min, max, samples); break;
            default:
                diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, $"unknown age kind '{kind}'", anchorRef, "$.age.kind")); break;
        }
    }
    else
    {
        diags.Add(new Diagnostic("incomplete-input", Diagnostic.Error, "age.kind is required (normal|uniform|samples)", anchorRef, "$.age.kind"));
    }

    if (root.TryGetProperty("reservoirOffset", out var ro) && ro.ValueKind == JsonValueKind.Object
        && ro.TryGetProperty("value", out var rov) && rov.TryGetDouble(out var roVal))
        offset = new Quantity(roVal, ro.TryGetProperty("unit", out var rou) ? rou.GetString() ?? "ka" : "ka", null);
    if (root.TryGetProperty("material", out var mt)) material = mt.GetString();
    if (root.TryGetProperty("method", out var md)) method = md.GetString();

    if (diags.Count > 0)
        return Results.Json(new { ok = false, diagnostics = diags }, JsonOptions.Pretty, statusCode: 400);

    lock (holder.Sync)
    {
        var before = Projector.Project(holder.Store);
        var beforeSig = SegmentSignature(before, DecisionPayload.MainBranch);
        var def = new AgeAnchorDef(anchorRef!, depth!, age!, offset, material, method, null);
        var raw = JsonOptions.Serialize(def);
        var ev = holder.Store.AppendEvent(ModelEvent.ImportMaterialKind,
            JsonOptions.Serialize(new { note = "anchor added via review UI", @ref = anchorRef }),
            "sha256:" + MaterialHashes.Sha256Hex(raw)[..16], before.Params.ParamVersion, Array.Empty<string>());
        var env = holder.Store.AddMaterial(MaterialEnvelope.AnchorKind, raw, ev.Seq);
        var after = Projector.Project(holder.Store);
        var afterSig = SegmentSignature(after, DecisionPayload.MainBranch);
        var affected = afterSig.Where(kv => !beforeSig.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var fit = after.Branches.First(b => b.Branch == DecisionPayload.MainBranch).Fit;
        holder.Store.AppendEvent(ModelEvent.FitKind, JsonOptions.Serialize(new
        {
            note = "local recompute after new anchor",
            inputDigest = fit.Model?.InputDigest,
            paramVersion = after.Params.ParamVersion,
            status = fit.Status.ToString(),
            affected
        }), fit.Model?.InputDigest ?? "none", after.Params.ParamVersion,
            after.Materials.Select(m => m.Id).ToList());
        return Results.Json(new
        {
            ok = true, id = env.Id, affected,
            fitStatus = fit.Status.ToString(),
            diagnostics = fit.Diagnostics
        }, JsonOptions.Pretty);
    }
});

// 人工决定（接受/否决/调整）——生成新的机器可读事件，旧事件永不覆盖
app.MapPost("/api/decision", async (HttpRequest req) =>
{
    var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var decision = root.TryGetProperty("decision", out var d) ? d.GetString() ?? "" : "";
    var target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
    var by = root.TryGetProperty("by", out var u) ? u.GetString() ?? "reviewer" : "reviewer";
    var branch = root.TryGetProperty("branch", out var b) ? b.GetString() ?? "main" : "main";
    if (decision is not ("accept" or "reject" or "adjust") || string.IsNullOrWhiteSpace(target))
        return Results.Json(new
        {
            ok = false,
            diagnostics = new[] { new Diagnostic("incomplete-input", Diagnostic.Error,
                "decision must be accept|reject|adjust and target is required", target, "$.decision") }
        }, JsonOptions.Pretty, statusCode: 400);
    lock (holder.Sync)
    {
        var state = Projector.Project(holder.Store);
        var payload = new DecisionPayload(decision, target, reason, by, branch);
        var ev = holder.Store.AppendEvent(ModelEvent.DecisionKind, JsonOptions.Serialize(payload),
            state.Branches.First(x => x.Branch == DecisionPayload.MainBranch).Fit.Model?.InputDigest ?? "none",
            state.Params.ParamVersion, new[] { target });
        return Results.Json(new { ok = true, eventId = ev.Id, seq = ev.Seq }, JsonOptions.Pretty);
    }
});

// 冻结当前模型；并发/重复冻结返回冲突（结构化结果）
app.MapPost("/api/freeze", async (HttpRequest req) =>
{
    var body = await JsonDocument.ParseAsync(req.Body);
    var root = body.RootElement;
    var branch = root.TryGetProperty("branch", out var b) ? b.GetString() ?? "main" : "main";
    var by = root.TryGetProperty("by", out var u) ? u.GetString() ?? "reviewer" : "reviewer";
    lock (holder.Sync)
    {
        var state = Projector.Project(holder.Store);
        var fit = state.Branches.FirstOrDefault(x => x.Branch == branch)?.Fit
                  ?? state.Branches[0].Fit;
        var digest = fit.Model?.InputDigest ?? "none";
        var outcome = holder.Store.TryFreeze(digest, branch, by, holder.Store.MaxSeq() + 1);
        if (outcome == FreezeOutcome.Frozen)
        {
            var payload = new FreezePayload(digest, branch, by, fit.Model!);
            var ev = holder.Store.AppendEvent(ModelEvent.FreezeKind, JsonOptions.Serialize(payload),
                digest, state.Params.ParamVersion, new[] { digest });
            return Results.Json(new { ok = true, outcome = "frozen", digest, eventId = ev.Id }, JsonOptions.Pretty);
        }
        var conflict = holder.Store.AppendEvent(ModelEvent.FreezeConflictKind,
            JsonOptions.Serialize(new { modelDigest = digest, branch, by, reason = "model already frozen; concurrent freeze rejected" }),
            digest, state.Params.ParamVersion, new[] { digest });
        return Results.Json(new { ok = false, outcome = "already-frozen", digest, eventId = conflict.Id }, JsonOptions.Pretty);
    }
});

app.MapGet("/api/export", () =>
{
    lock (holder.Sync)
        return Results.Text(holder.Store.ExportJson(), "application/json; charset=utf-8");
});

app.MapPost("/api/import", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync();
    lock (holder.Sync)
    {
        var result = holder.Store.ImportJson(json);
        return result switch
        {
            ImportResult.Ok ok => Results.Json(new { ok = true, ok.Materials, ok.Events, ok.Freezes }, JsonOptions.Pretty),
            ImportResult.CorruptFormat c => Results.Json(new { ok = false, error = "corrupt-format", c.Reason, c.Position }, JsonOptions.Pretty, statusCode: 422),
            _ => Results.StatusCode(500)
        };
    }
});

// 删除本地数据库（之后可用导出文件重新导入，身份/顺序/来源关系保持）
app.MapPost("/api/reset", () =>
{
    lock (holder.Sync)
    {
        holder.Reset();
        return Results.Json(new { ok = true, note = "local database deleted; import an export or re-seed the case pack" }, JsonOptions.Pretty);
    }
});

app.MapPost("/api/seed", () =>
{
    lock (holder.Sync)
    {
        if (holder.Store.Events().Count > 0)
            return Results.Json(new { ok = false, note = "database not empty" }, JsonOptions.Pretty);
        var report = CaseRunner.Run(CasePack.Load(File.ReadAllText(casePath)), holder.Store);
        return Results.Json(new { ok = true, report.Flags }, JsonOptions.Pretty);
    }
});

app.Run();

static Dictionary<string, string> SegmentSignature(ProjectedState state, string branch)
{
    var fit = state.Branches.First(b => b.Branch == branch).Fit;
    return fit.Model?.Segments.ToDictionary(s => s.Segment,
        s => s.Status + ":" + string.Join(",", s.Candidates.Select(c => c.Id)))
        ?? new Dictionary<string, string>();
}

namespace DepthAge.Server
{
    /// <summary>可替换的存储句柄：reset 时关闭旧库、删除文件、重建空库。</summary>
    public sealed class StoreHolder
    {
        public object Sync { get; } = new();
        public string Path { get; }
        public DepthAgeStore Store { get; private set; }

        public StoreHolder(string path)
        {
            Path = path;
            Store = new DepthAgeStore(path);
        }

        public void Reset()
        {
            Store.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(Path + suffix); } catch { }
            Store = new DepthAgeStore(Path);
        }
    }
}
