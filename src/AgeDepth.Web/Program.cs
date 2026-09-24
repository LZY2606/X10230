using AgeDepth.CasePack;
using AgeDepth.Domain;
using AgeDepth.Store;

var dbPath = Environment.GetEnvironmentVariable("AGEDEPTH_DB") ?? "agedepth.db";
var store = new EventStore(dbPath);
var svc = new AppService(store);

if (svc.Events().Count == 0)
{
    var pack = Path.Combine(AppContext.BaseDirectory, "casepack", "core-chron-scale.json");
    if (!File.Exists(pack))
        pack = Path.Combine(Directory.GetCurrentDirectory(), "casepack", "core-chron-scale.json");
    CasePackLoader.Replay(svc, pack);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(svc);
var app = builder.Build();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/index.html"));

app.MapGet("/api/state", (long? at) =>
{
    var s = svc.State(at);
    return Results.Json(new
    {
        atSeq = s.LastSeq,
        modelSetVersion = svc.ModelSetVersion,
        s.Datums, s.Sections, s.Boundaries, s.Anchors, s.Offsets, s.EventLayers,
        s.Exclusions, s.Params, s.Decisions, s.Freezes, s.Impacts,
        compute = s.LatestCompute,
        derived = svc.DerivedIndex(),
        events = svc.Events(at).Select(e => new { e.Seq, e.Id, e.Type, e.At })
    });
});

app.MapPost("/api/freeze", (FreezeReq r) => Results.Json(svc.Freeze(r.By, r.ExpectedVersion)));

app.MapPost("/api/anchors", (AgeAnchor a) =>
{
    var (evtId, impact) = svc.AddAnchorWithImpact(a);
    return Results.Json(new { evtId, impact });
});

app.MapPost("/api/decisions", (DecisionReq r) =>
    Results.Json(new { id = svc.RecordDecision(r.TargetId, r.Action, r.Rationale, r.DecidedBy) }));

app.MapPost("/api/params", (ParamReq r) =>
    Results.Json(new { id = svc.DefineParams(r.Version, r.ToleranceYr, r.Note) }));

app.MapPost("/api/exclusions", (ExclusionRecord rec) =>
    Results.Json(new { id = svc.RecordExclusion(rec) }));

app.MapGet("/api/export", () =>
    Results.Text(svc.Export(), "application/json"));

app.MapPost("/api/import", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync();
    try { svc.Import(json); return Results.Json(new { status = "imported" }); }
    catch (FormatCorruptException ex) { return Results.BadRequest(new { status = "format-corrupt", ex.Message }); }
});

app.Run();

record FreezeReq(string By, long ExpectedVersion);
record DecisionReq(string TargetId, string Action, string Rationale, string DecidedBy);
record ParamReq(string Version, double ToleranceYr, string Note);
