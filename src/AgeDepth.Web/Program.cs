using AgeDepth.Core;
using AgeDepth.Web;
using System.Text.Json;

var contentRoot = Directory.GetCurrentDirectory();

if (args.Contains("emit-casepack"))
{
    Console.WriteLine(CasePack.Build().ToJson());
    return;
}

var dbPath = Path.Combine(contentRoot, "agedepth.db");
var store = new Store(dbPath);
var workspace = new Workspace(store);

if (store.Count("materials") == 0)
{
    var packPath = Path.Combine(contentRoot, "casepack", "casepack.json");
    CasePackData pack;
    if (File.Exists(packPath))
    {
        var parsed = CasePackData.FromJson(File.ReadAllText(packPath));
        pack = parsed.IsOk && parsed.Value is not null ? parsed.Value : CasePack.Build();
    }
    else
    {
        pack = CasePack.Build();
    }
    workspace.SeedAndReplay(pack);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(workspace);
var app = builder.Build();
var jsonOptions = CasePackData.JsonOptions;

app.MapGet("/", (string? candidate) =>
{
    var html = ReviewPage.Render(workspace, candidate);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/state", () =>
{
    var run = workspace.LatestRun();
    var (version, frozen) = store.ModelState(CasePack.ModelId);
    return Results.Json(new
    {
        run,
        candidates = store.Candidates(),
        model = new { modelId = CasePack.ModelId, version, frozen },
        parameters = workspace.CurrentParameters(),
        excludedAnchors = workspace.ExcludedAnchorIds()
    }, jsonOptions);
});

app.MapGet("/api/events", () =>
    Results.Json(store.Events().Select(e => new { e.Seq, e.Id, e.Type, payload = JsonDocument.Parse(e.Payload).RootElement, e.Parent }), jsonOptions));

app.MapPost("/api/anchor", async (HttpContext ctx) =>
{
    var anchor = await ctx.ReadFromJsonAsync<AgeAnchor>(jsonOptions);
    if (anchor is null)
        return Results.Json(new { kind = "FormatCorrupt", message = "anchor body missing or invalid" }, statusCode: 400);
    var result = workspace.AddAnchor(anchor);
    return result.IsOk
        ? Results.Json(new { kind = "Ok", runId = result.Value.Run.RunId })
        : Results.Json(new { kind = result.Kind.ToString(), diagnostics = result.Diagnostics }, statusCode: 422);
});

app.MapPost("/api/compare", async (HttpContext ctx) =>
{
    var anchor = await ctx.ReadFromJsonAsync<AgeAnchor>(jsonOptions);
    if (anchor is null)
        return Results.Json(new { kind = "FormatCorrupt", message = "anchor body missing or invalid" }, statusCode: 400);
    var baseline = workspace.LatestRun();
    var compared = workspace.CompareWithNewAnchor(anchor);
    if (!compared.IsOk || compared.Value is null || baseline is null)
        return Results.Json(new { kind = compared.Kind.ToString(), diagnostics = compared.Diagnostics }, statusCode: 422);
    var delta = Comparison.Summarize(baseline, compared.Value, anchor);
    return Results.Json(delta, jsonOptions);
});

app.MapPost("/api/exclude", async (HttpContext ctx) =>
{
    var body = await ctx.ReadFromJsonAsync<Dictionary<string, string>>();
    if (body is null || !body.TryGetValue("anchorId", out var id))
        return Results.BadRequest(new { kind = "FormatCorrupt", message = "anchorId required" });
    var eventId = workspace.ExcludeAnchor(id, body.GetValueOrDefault("reason", "manual exclusion"));
    return Results.Json(new { kind = "Ok", eventId });
});

app.MapPost("/api/params", async (HttpContext ctx) =>
{
    var body = await ctx.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    if (body is null)
        return Results.BadRequest(new { kind = "FormatCorrupt" });
    var current = workspace.CurrentParameters();
    var iterations = body.TryGetValue("iterations", out var it) ? it.GetInt32() : current.Iterations;
    var gridStep = body.TryGetValue("gridStep", out var gs) ? gs.GetDouble() : current.GridStep;
    var version = "v" + (int.Parse(current.ParamVersion.TrimStart('v')) + 1);
    var result = workspace.ReviseParameters(current with { ParamVersion = version, Iterations = iterations, GridStep = gridStep },
        body.TryGetValue("reason", out var r) ? r.GetString() ?? "parameter revision" : "parameter revision");
    return result.IsOk
        ? Results.Json(new { kind = "Ok", runId = result.Value!.RunId, paramVersion = version })
        : Results.Json(new { kind = result.Kind.ToString(), diagnostics = result.Diagnostics }, statusCode: 422);
});

app.MapPost("/api/freeze", async (HttpContext ctx) =>
{
    var body = await ctx.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    var expected = body is not null && body.TryGetValue("expectedVersion", out var v) ? v.GetInt32() : 0;
    var result = workspace.Freeze(CasePack.ModelId, expected);
    return result.IsOk
        ? Results.Json(new { kind = "Ok", eventId = result.Value })
        : Results.Json(new { kind = result.Kind.ToString(), diagnostics = result.Diagnostics }, statusCode: 409);
});

app.MapPost("/api/unfreeze", async (HttpContext ctx) =>
{
    var body = await ctx.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    var expected = body is not null && body.TryGetValue("expectedVersion", out var v) ? v.GetInt32() : 0;
    if (!store.TrySetFrozen(CasePack.ModelId, expected, false))
        return Results.Json(new { kind = "Conflict" }, statusCode: 409);
    store.AppendEvent("decision.unfreeze", JsonSerializer.Serialize(new { modelId = CasePack.ModelId }, jsonOptions));
    return Results.Json(new { kind = "Ok" });
});

app.MapPost("/api/candidate/decide", async (HttpContext ctx) =>
{
    var body = await ctx.ReadFromJsonAsync<Dictionary<string, string>>();
    if (body is null || !body.TryGetValue("candidateId", out var candidateId))
        return Results.BadRequest(new { kind = "FormatCorrupt", message = "candidateId required" });
    var accept = body.GetValueOrDefault("decision", "accept") == "accept";
    var eventId = workspace.DecideCandidate(candidateId, accept,
        body.GetValueOrDefault("reviewer", "reviewer"), body.GetValueOrDefault("rationale", ""));
    return Results.Json(new { kind = "Ok", eventId });
});

app.MapGet("/export", () =>
    Results.Content(store.Export(), "application/json; charset=utf-8"));

app.MapPost("/api/reset", () =>
{
    store.Reset();
    return Results.Json(new { kind = "Ok" });
});

app.MapPost("/api/import", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var result = store.Import(body);
    return result.IsOk
        ? Results.Json(new { kind = "Ok", result.Value })
        : Results.Json(new { kind = result.Kind.ToString(), diagnostics = result.Diagnostics }, statusCode: 422);
});

app.Run();

public partial class Program { }
