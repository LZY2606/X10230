using System.Text.Json;
using AgeModel;

var dbPath = Environment.GetEnvironmentVariable("AGEMODEL_DB") ?? "agemodel.db";
var store = new Store(dbPath);
var svc = new Service(store);

// 首次启动自动播种离线案例包（岩芯年代尺）
if (store.NextSeqPeek() == 0)
{
    var log = CaseRunner.Replay(svc);
    store.AppendEvent("case-seeded", new { caseId = "core-age-scale-kx01", steps = log.Count });
}

var app = WebApplication.CreateBuilder(args).Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (long? asOf) => Results.Json(svc.StateAsOf(asOf), Json.Options));

app.MapGet("/api/export", () => Results.Text(store.ExportArchive(), "application/json"));

app.MapPost("/api/import", async (HttpRequest req) =>
{
    var els = await JsonSerializer.DeserializeAsync<JsonElement[]>(req.Body, Json.Options);
    if (els is null) return Results.BadRequest(new { error = "empty body" });
    var mats = new List<Material>();
    try { foreach (var el in els) mats.Add(CaseRunner.ParseMaterial(el)); }
    catch (InvalidDataException e) { return Results.Json(new { status = "FormatCorrupt", error = e.Message }); }
    var r = svc.ImportMaterials(mats, "api");
    return Results.Json(r, Json.Options);
});

app.MapPost("/api/decision", async (HttpRequest req) =>
{
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, Json.Options);
    var e = svc.Decide(b.GetProperty("targetId").GetString()!, b.GetProperty("accept").GetBoolean(),
        b.GetProperty("reason").GetString()!, b.GetProperty("reviewer").GetString()!);
    return Results.Json(e, Json.Options);
});

app.MapPost("/api/freeze", async (HttpRequest req) =>
{
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, Json.Options);
    var r = svc.Freeze(b.GetProperty("modelId").GetString()!, b.GetProperty("by").GetString()!);
    return r.Status == OutcomeStatus.Conflict
        ? Results.Conflict(Json.Ser(r))
        : Results.Json(r, Json.Options);
});

app.MapPost("/api/params", async (HttpRequest req) =>
{
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, Json.Options);
    var e = svc.AdjustParams(b.GetProperty("tolerance").GetDouble(), b.GetProperty("maxCandidates").GetInt32(),
        b.GetProperty("budget").GetInt32(), b.GetProperty("by").GetString()!);
    return Results.Json(e, Json.Options);
});

app.MapPost("/api/propose", async (HttpRequest req) =>
{
    var el = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, Json.Options);
    var m = (RadioAge)CaseRunner.ParseMaterial(el);
    var r = svc.ProposeAnchor(m);
    return Results.Json(r, Json.Options);
});

app.MapPost("/api/import-archive", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var r = store.ImportArchive(body);
    return Results.Json(r, Json.Options);
});

app.MapPost("/api/recover", () => Results.Json(new { cleared = store.Recover() }));

app.MapPost("/api/case/reset", () =>
{
    var fresh = Path.Combine(Path.GetTempPath(), $"agemodel-reset-{Guid.NewGuid():N}.db");
    // 重置 = 导出当前档案备查后清空重来（此处直接重放到新库再换入过于复杂，采用重放追加语义）
    var log = CaseRunner.Replay(svc);
    return Results.Json(new { replayed = log.Count, steps = log });
});

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

public partial class Program { }
