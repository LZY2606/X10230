using System.Text;
using System.Text.Json;
using ChronoCore;
using ChronoStorage;
using ChronoWeb;

var builder = WebApplication.CreateBuilder(args);
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "chrono.db");
builder.Services.AddSingleton(_ => new ChronoRepository(dbPath));
var app = builder.Build();

var repo = app.Services.GetRequiredService<ChronoRepository>();
if (!repo.ProjectExists())
{
    new WorkflowSeeder(repo).Seed();
    CasePackageTool.WriteToDirectory(Path.Combine(builder.Environment.ContentRootPath, "cases"));
}

app.MapGet("/", (string? branch, long? at, string? flash, string? kind) =>
    Results.Text(ReviewPage.Render(repo, branch ?? "main", at, flash, kind), "text/html; charset=utf-8"));

app.MapGet("/svg", (string branch, long? at) =>
{
    var model = ReviewPage.Build(repo, branch, at);
    return Results.Text(SvgRender.RenderProfile(model.State, model.Current, branch), "image/svg+xml");
});

app.MapPost("/actions/derive", (HttpContext ctx) =>
{
    var form = ctx.Request.Form;
    var branch = form["branch"].ToString();
    var paramVersion = form["parameterVersion"].ToString();
    var actor = form["actor"].ToString(); if (string.IsNullOrWhiteSpace(actor)) actor = "reviewer";
    var events = repo.ReadAllEvents();
    var state = EventFolder.FoldTo(events, branch, null);
    var p = state.Parameters.TryGetValue(paramVersion, out var pp) ? pp : state.Parameters[state.CurrentParameterVersion];
    var svc = new ChronoAppService(new SqliteEventSink(repo));
    var outcome = svc.DeriveAndRecord(branch, state, p, actor: actor);
    return Results.Redirect($"/?branch={branch}&kind=info&flash=" +
        Uri.EscapeDataString($"派生完成：{outcome.Kind}，候选 {outcome.Candidates.Count} 套，等优 {outcome.Candidates.Count(m => m.Tie)} 套"));
});

app.MapPost("/actions/freeze", (HttpContext ctx) =>
{
    var form = ctx.Request.Form;
    var branch = form["branch"].ToString();
    var modelId = form["modelId"].ToString();
    var actor = string.IsNullOrWhiteSpace(form["actor"]) ? "reviewer" : form["actor"].ToString();
    var events = repo.ReadAllEvents();
    var state = EventFolder.FoldTo(events, branch, null);
    var model = repo.ReadDerivedModels(branch).FirstOrDefault(m => m.Id == modelId);
    var svc = new ChronoAppService(new SqliteEventSink(repo));
    var ok = svc.TryFreeze(branch, modelId, model?.InputDigest ?? "", model?.ParameterVersion ?? "v2",
        actor, out var conflict, out var holder);
    var msg = ok ? $"已冻结模型 {modelId}（{actor}），之后新锚点只能做影响比较"
                : $"并发冻结被拒：{modelId} 已由 {holder} 持有；冲突已记录为未采纳事件";
    return Results.Redirect($"/?branch={branch}&kind={(ok ? "info" : "warn")}&flash=" + Uri.EscapeDataString(msg));
});

app.MapPost("/actions/impact", (HttpContext ctx) =>
{
    var form = ctx.Request.Form;
    var branch = form["branch"].ToString();
    var modelId = form["modelId"].ToString();
    var depth = form["depth"].ToString();
    var mean = double.Parse(form["meanCalABP"]!);
    var sd = double.Parse(form["sdCalABP"]!);
    var adopt = form["adopt"] == "on";
    var events = repo.ReadAllEvents();
    var state = EventFolder.FoldTo(events, branch, null);
    var frozen = repo.ReadDerivedModels(branch).First(m => m.Id == modelId);
    var trial = new Anchor("an-trial-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        "PL-X-试验", depth, "dat-mbsf", SamplesFactory.Normal(mean, sd), "AMS14C",
        900 + (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1000), "res-local", null, null,
        "审阅页冻结后试验性新锚点");
    var engine = new DerivationEngine();
    var report = engine.CompareNewAnchor(state, frozen, trial, null, state.Parameters["v2"]);
    var svc = new ChronoAppService(new SqliteEventSink(repo));
    svc.RecordImpact(branch, report, adopt);
    var msg = $"影响比较完成：单调保持={report.MonotonicPreserved}；受影响段=" +
              $"{string.Join(",", report.AffectedIntervalIds) ?? "（无）"}；" +
              $"最大中位年龄偏移={report.MaxAgeShiftCalA:0} cal a；结论{(adopt ? "已采纳" : "仅记录未采纳")}";
    return Results.Redirect($"/?branch={branch}&kind={(report.MonotonicPreserved ? "info" : "warn")}&flash=" + Uri.EscapeDataString(msg));
});

app.MapPost("/actions/decision", (HttpContext ctx) =>
{
    var form = ctx.Request.Form;
    var branch = form["branch"].ToString();
    var modelId = form["modelId"].ToString();
    var kind = form["decisionKind"].ToString();
    var reason = form["reason"].ToString();
    var actor = string.IsNullOrWhiteSpace(form["actor"]) ? "reviewer" : form["actor"].ToString();
    var svc = new ChronoAppService(new SqliteEventSink(repo));
    var id = "dec-web-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    svc.RecordDecision(branch, new Decision(id, kind, modelId, null, reason, actor, 0), false, null);
    return Results.Redirect($"/?branch={branch}&kind=info&flash=" + Uri.EscapeDataString($"新决定 {kind} 已生成事件 {id}"));
});

app.MapGet("/export", () =>
{
    var bytes = repo.ExportBundle("CORESITE-7B 深度年龄模型项目");
    return Results.File(bytes, "application/zip", "pairwise-chrono-export.zip");
});

app.MapPost("/import", async (HttpContext ctx) =>
{
    var file = ctx.Request.Form.Files.FirstOrDefault();
    if (file is null)
        return Results.Redirect("/?kind=warn&flash=" + Uri.EscapeDataString("未选择导出包"));
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    try
    {
        var stats = repo.ImportBundle(ms.ToArray(), replaceExisting: true);
        return Results.Redirect($"/?kind=info&flash=" + Uri.EscapeDataString(
            $"导入成功：{stats.Events} 事件 / {stats.Branches} 分支 / {stats.Derived} 派生模型，身份与顺序已校验"));
    }
    catch (FormatCorruptedImportException ex)
    {
        return Results.Redirect("/?kind=error&flash=" + Uri.EscapeDataString(
            "格式损坏（整体回滚，未写入任何数据）：" + ex.Message + " 位置:" + ex.Location));
    }
});

app.MapPost("/actions/reset", () =>
{
    repo.DeleteAllData();
    new WorkflowSeeder(repo).Seed();
    return Results.Redirect("/?kind=info&flash=" + Uri.EscapeDataString("已删除全部本地数据并重新播种样例"));
});

app.Run();
