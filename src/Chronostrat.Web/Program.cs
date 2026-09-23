using System.Text.Json.Nodes;
using Chronostrat.Core;
using Chronostrat.Web;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string dbPath = Environment.GetEnvironmentVariable("CHRONOSTRAT_DB")
    ?? Path.Combine(app.Environment.ContentRootPath, "chronostrat.db");
var log = new SqliteEventLog(dbPath);
var session = new Session(log);

// Seed the offline case package on first run — no account, no cloud, no config.
if (log.CurrentSeq == 0)
{
    string casePath = Path.Combine(app.Environment.ContentRootPath, "cases", "core-age-scale-case.json");
    var caseDoc = JsonNode.Parse(await File.ReadAllTextAsync(casePath))!.AsObject();
    CaseRunner.Run(session, caseDoc);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Json(new { ok = true }));

app.MapGet("/api/state", (long? atSeq) =>
{
    var state = session.StateAt(atSeq);
    var snapshot = session.Snapshot(atSeq);
    state["materials"] = MaterialsView.ToJson(snapshot);
    state["events"] = new JsonArray(session.Events(atSeq).Select(e => (JsonNode)new JsonObject
    {
        ["id"] = e.Id,
        ["seq"] = e.Seq,
        ["type"] = e.Type,
        ["paramVersion"] = e.ParamVersion,
        ["inputDigest"] = e.InputDigest,
        ["dependsOn"] = new JsonArray(e.DependsOn.Select(JsonValue.Create).ToArray()),
        ["recordedAt"] = e.RecordedAt,
        ["summary"] = Summaries.Of(e)
    }).ToArray());
    state["case"] = CaseView.Info();
    return Results.Json(state);
});

app.MapPost("/api/import", async (HttpRequest req) =>
{
    var material = (await JsonNode.ParseAsync(req.Body))!.AsObject();
    var (evt, diags) = session.ImportMaterial(material);
    return Results.Json(new JsonObject
    {
        ["eventId"] = evt.Id,
        ["seq"] = evt.Seq,
        ["diagnostics"] = new JsonArray(diags.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code, ["message"] = d.Message, ["position"] = d.Position, ["severity"] = d.Severity
        }).ToArray())
    });
});

app.MapPost("/api/derive", () =>
{
    var (outcome, evt) = session.Derive();
    return outcome switch
    {
        DeriveOutcome.Derived d => Results.Json(new JsonObject
        {
            ["outcome"] = "derived",
            ["eventId"] = evt!.Id,
            ["modelSet"] = ModelSetJson.ToJson(d.Models)
        }),
        DeriveOutcome.BudgetExhausted b => Results.Json(new JsonObject
        {
            ["outcome"] = "budget-exhausted", ["position"] = b.Position, ["spent"] = b.Spent, ["needed"] = b.Needed
        }),
        DeriveOutcome.SemanticUnsolvable s => Results.Json(new JsonObject
        {
            ["outcome"] = "semantic-unsolvable", ["position"] = s.Position, ["reason"] = s.Reason
        }),
        _ => Results.StatusCode(500)
    };
});

app.MapPost("/api/parameters", async (HttpRequest req) =>
{
    var body = (await JsonNode.ParseAsync(req.Body))!.AsObject();
    var evt = session.ReviseParameters(new DeriveParameters(
        body["version"]!.GetValue<int>(),
        body["mcBudget"]!.GetValue<int>(),
        body["minValidDraws"]!.GetValue<int>(),
        body["equivalenceTolerance"]!.GetValue<double>(),
        body["seed"]!.GetValue<int>()),
        body["note"]?.GetValue<string>() ?? "");
    return Results.Json(new { eventId = evt.Id, seq = evt.Seq });
});

app.MapPost("/api/freeze", async (HttpRequest req) =>
{
    var body = (await JsonNode.ParseAsync(req.Body))!.AsObject();
    var result = session.Freeze(
        body["modelSetId"]!.GetValue<string>(),
        body["by"]!.GetValue<string>(),
        body["expectedSeq"]!.GetValue<long>());
    return Results.Json(new JsonObject
    {
        ["ok"] = result.Ok,
        ["eventId"] = result.Event?.Id,
        ["conflict"] = result.ConflictReason
    });
});

app.MapPost("/api/decide", async (HttpRequest req) =>
{
    var body = (await JsonNode.ParseAsync(req.Body))!.AsObject();
    string kind = body["kind"]!.GetValue<string>();
    var evt = session.Decide(
        kind == "accept" ? EventTypes.CandidateAccepted : EventTypes.CandidateRejected,
        body["candidateId"]!.GetValue<string>(),
        body["rationale"]!.GetValue<string>(),
        body["by"]!.GetValue<string>());
    return Results.Json(new { eventId = evt.Id, seq = evt.Seq });
});

app.MapPost("/api/local-recompute", async (HttpRequest req) =>
{
    var body = (await JsonNode.ParseAsync(req.Body))!.AsObject();
    var affected = body["affectedSegments"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    var (evt, outcome, _) = session.LocalRecompute(body["triggerEventId"]!.GetValue<string>(), affected);
    return Results.Json(new JsonObject
    {
        ["eventId"] = evt.Id,
        ["outcome"] = outcome is DeriveOutcome.Derived ? "derived" : outcome.GetType().Name
    });
});

app.MapGet("/api/export", () =>
{
    string archive = log.ExportArchive();
    return Results.Text(archive, "application/json");
});

app.MapPost("/api/import-archive", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    string text = await reader.ReadToEndAsync();
    var outcome = log.ImportArchive(text);
    return outcome switch
    {
        ArchiveOutcome.Ok ok => Results.Json(new JsonObject { ["outcome"] = "ok", ["eventCount"] = ok.EventCount }),
        ArchiveOutcome.FormatCorrupt fc => Results.Json(new JsonObject
        {
            ["outcome"] = "format-corrupt", ["position"] = fc.Position, ["reason"] = fc.Reason
        }),
        _ => Results.StatusCode(500)
    };
});

app.Run();

public static class Summaries
{
    public static string Of(Event e) => e.Type switch
    {
        EventTypes.MaterialImported => $"{e.Payload["material"]?["kind"]?.GetValue<string>()}:{e.Payload["material"]?["id"]?.GetValue<string>()}",
        EventTypes.ModelDerived => $"modelSet={e.Payload["id"]?.GetValue<string>()} candidates={e.Payload["candidates"]?.AsArray().Count}",
        EventTypes.ModelFrozen => $"frozen={e.Payload["modelSetId"]?.GetValue<string>()} by={e.Payload["frozenBy"]?.GetValue<string>()}",
        EventTypes.FreezeRejected => $"rejected for={e.Payload["frozenBy"]?.GetValue<string>()}",
        EventTypes.ParameterRevised => $"v{e.Payload["version"]?.GetValue<int>()} {e.Payload["note"]?.GetValue<string>()}",
        EventTypes.CandidateAccepted => $"accept {e.Payload["candidateId"]?.GetValue<string>()} by={e.Payload["decidedBy"]?.GetValue<string>()}",
        EventTypes.CandidateRejected => $"reject {e.Payload["candidateId"]?.GetValue<string>()} by={e.Payload["decidedBy"]?.GetValue<string>()}",
        EventTypes.LocalRecompute => $"affected=[{string.Join(",", e.Payload["affectedSegments"]!.AsArray().Select(x => x!.GetValue<string>()))}]",
        EventTypes.RuleVerdict => $"{e.Payload["rule"]?.GetValue<string>()} -> {e.Payload["verdict"]?.GetValue<string>()}",
        _ => ""
    };
}
