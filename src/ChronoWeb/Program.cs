using System.Text.Json;
using ChronoCore.CasePack;
using ChronoCore.Domain;
using ChronoCore.Engine;
using ChronoCore.Storage;
using ChronoCore.Web;
using ChronoWeb.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CaseSeeder>();
var app = builder.Build();

app.UseStaticFiles();
var seeder = app.Services.GetRequiredService<CaseSeeder>();
seeder.EnsureSeeded();
var pid = CasePackBuilder.ProjectId;
var transfer = new ProjectTransfer();

object Wrap(object? data, string? error = null) => new { ok = error is null, error, data };

app.MapGet("/", () => Results.Redirect("/review"));

app.MapGet("/review", (CaseSeeder s) =>
{
    var p = s.Workflow.Require(pid);
    var trial = s.Workflow.LatestResult(pid, "trial-new-anchor");
    var freezeEvent = p.Branches.First(b => b.Id == "main").FrozenAtEventId!;
    var freezeResult = s.Workflow.ResultAt(pid, freezeEvent)!;
    var html = Pages.ReviewPage(p, freezeResult, trial, freezeResult);
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/project", (CaseSeeder s) =>
{
    var p = s.Workflow.Require(pid);
    return Results.Json(Wrap(new
    {
        p.Id, p.Name,
        branches = p.Branches,
        events = p.Events.Select(e => new { e.Seq, e.Id, e.BranchId, e.ParentEventId, e.Type, e.ParameterSetId, e.InputDigest, e.ResultRef, e.CreatedAt }),
        materials = p.Materials.Select(ProjectDto.Material),
        decisions = p.Decisions,
        parameterSets = p.ParameterSets,
        script = CasePackBuilder.BuildInitial().WithScript().Script
    }), JsonOptions.Web);
});

app.MapGet("/api/result/{eventId}", (string eventId, CaseSeeder s) =>
{
    var r = s.Workflow.ResultAt(pid, eventId);
    return r is null ? Results.NotFound(Wrap(null, "事件不存在或无结果")) : Results.Json(Wrap(r), JsonOptions.Web);
});

app.MapGet("/api/svg/{eventId}", (string eventId, string? candidate, CaseSeeder s) =>
{
    var r = s.Workflow.ResultAt(pid, eventId);
    if (r is null) return Results.NotFound();
    return Results.Content(SvgViewRenderer.Render(r, candidate ?? r.Candidates[0].CandidateId), "image/svg+xml");
});

app.MapGet("/api/display-values/{eventId}", (string eventId, CaseSeeder s) =>
{
    var p = s.Workflow.Require(pid);
    var r = s.Workflow.ResultAt(pid, eventId);
    if (r is null) return Results.NotFound(Wrap(null, "事件不存在"));
    return Results.Json(Wrap(DisplayValueRegistry.ForResult(r, p.Materials)), JsonOptions.Web);
});

app.MapPost("/api/decision", async (HttpRequest req, CaseSeeder s) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var branch = root.GetProperty("branchId").GetString() ?? "main";
    var kind = Enum.Parse<DecisionKind>(root.GetProperty("kind").GetString()!, true);
    var target = root.GetProperty("targetMaterialId").GetString();
    var reason = root.GetProperty("reason").GetString() ?? "";
    var paramId = root.TryGetProperty("parameterSetId", out var pv) && pv.ValueKind == JsonValueKind.String
        ? pv.GetString()! : "params-v2";
    var payload = JsonSerializer.Serialize(new { kind, target, reason, ts = DateTime.UtcNow });
    var resp = s.Workflow.RecordDecision(pid, branch, new Decision
    {
        Kind = kind, Status = DecisionStatus.Accepted, TargetMaterialId = target,
        ParameterSetId = paramId, Reason = reason, PayloadJson = payload
    }, new { api = "decision", kind, target });
    return Results.Json(Wrap(new
    {
        outcome = resp.Outcome.ToString(), resp.Message,
        evt = resp.Event, result = resp.Result,
        diagnostics = resp.Result?.Diagnostics
    }), JsonOptions.Web);
});

app.MapPost("/api/freeze", (string? branchId, CaseSeeder s) =>
{
    var resp = s.Workflow.Freeze(pid, branchId ?? "main");
    return Results.Json(Wrap(new { outcome = resp.Outcome.ToString(), resp.Message, evt = resp.Event, result = resp.Result }), JsonOptions.Web);
});

app.MapPost("/api/trial-anchor", async (HttpRequest req, CaseSeeder s) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;
    var depth = root.GetProperty("depthRaw").GetString()!;
    var age = root.GetProperty("ageRaw").GetString()!;
    var sigma = root.GetProperty("sigmaRaw").GetString()!;
    var trialId = root.TryGetProperty("trialBranchId", out var tv) ? tv.GetString()! : "trial-review-" + Guid.NewGuid().ToString("N")[..6];
    var anchor = new Material
    {
        Id = "mat-trial-" + Guid.NewGuid().ToString("N")[..8],
        Kind = MaterialKind.RadiometricDate, SourceRef = "ui/trial-anchor-form",
        SourceFragment = $"trial,{depth}", SourceLine = 0, ImportedAt = DateTime.UtcNow,
        DepthDatum = DepthDatum.Mbsf,
        DepthRaw = Measurement.ParseDepth(depth, DepthDatum.Mbsf),
        AgeRaw = Measurement.ParseAge(age, TimeScale.Bp1950),
        OneSigmaRaw = Measurement.ParseAge(sigma, TimeScale.Bp1950),
        Distribution = "normal", MaterialType = "marine", ReservoirId = "mat-res-02"
    };
    if (!anchor.DepthRaw.IsConvertible || !anchor.AgeRaw.IsConvertible)
        return Results.BadRequest(Wrap(new
        {
            outcome = "InputIncomplete",
            diagnostics = new[] { anchor.DepthRaw.Diagnostic, anchor.AgeRaw.Diagnostic }.Where(x => x is not null)
        }, "输入不完整：返回带位置的诊断，不以空值或默认值伪装成功"));

    var resp = s.Workflow.CreateTrialFromFreeze(pid, "main", trialId, anchor, new { api = "trial-anchor", depth, age });
    return Results.Json(Wrap(new
    {
        outcome = resp.Outcome.ToString(), resp.Message, trialBranchId = trialId,
        evt = resp.Event, result = resp.Result,
        affectedScope = resp.Result?.RecomputedSegmentIds
    }), JsonOptions.Web);
});

app.MapGet("/api/archive", (CaseSeeder s) =>
{
    var archive = new CounterfactualArchive(s.Workflow, pid);
    var baseline = archive.EstablishBaseline();
    archive.RunSingleSourceChange("单源改动：重测 mat-date-06（局部重算）", "mat-date-06", m =>
    {
        m.AgeRaw = Measurement.ParseAge("13490 yr BP", TimeScale.Bp1950);
        m.OneSigmaRaw = Measurement.ParseAge("120 yr", TimeScale.Bp1950);
    }, new[] { "local-recompute" }, ArchiveAdoption.Adopted, "采纳：新测量值更贴合 ASH-1580 等时面");
    archive.RunSingleSourceChange("单源改动：删除 U1 不整合边界（未采纳，检验越界）", "mat-seg-03", m =>
    {
        m.BoundaryAtBase = BoundaryKind.Continuous;
        m.Id = "mat-seg-03";
    }, new[] { "unconformity", "scope-boundary" }, ArchiveAdoption.NotAdopted, "未采纳：会让 L2/L3 跨缺失带插值，影响越出局部范围");
    archive.RunSingleSourceChange("单源改动：抬高长尾权重（未采纳）", "mat-date-07", m =>
    {
        m.TailWeight = 0.6;
    }, new[] { "age-long-tail" }, ArchiveAdoption.NotAdopted, "未采纳：旧碳尾质量缺乏新证据支撑");
    return Results.Json(Wrap(archive.Runs.Select(r => new
    {
        r.RunId, r.Label, r.ChangedSourceMaterialId, r.ChangeDescription,
        tags = r.BoundaryTags, adoption = r.Adoption.ToString(),
        r.BaselineResultId, r.ResultId, r.Verdicts,
        affected = r.AffectedValues.Take(60),
        affectedCount = r.AffectedValues.Count,
        outOfScopeCount = r.AffectedValues.Count(a => !a.WithinExpectedScope)
    })), JsonOptions.Web);
});

app.MapGet("/api/export", (CaseSeeder s) =>
{
    var p = s.Workflow.Require(pid);
    var json = transfer.Export(p);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8",
        $"chrono-{pid}-export.json");
});

app.MapPost("/api/import-roundtrip", async (HttpRequest req, CaseSeeder s) =>
{
    using var sr = new StreamReader(req.Body);
    var body = await sr.ReadToEndAsync();
    string json;
    if (string.IsNullOrWhiteSpace(body))
    {
        var p = s.Workflow.Require(pid);
        json = transfer.Export(p);
    }
    else json = body;

    var probeDb = Path.Combine(Path.GetDirectoryName(s.DbPath)!, "roundtrip-" + Guid.NewGuid().ToString("N")[..8] + ".sqlite");
    var report = transfer.Import(json, probeDb);
    var verified = report.Outcome == TransferOutcome.Imported;
    if (verified)
    {
        var reloaded = new ProjectRepository(probeDb).LoadProject(pid);
        var orig = s.Workflow.Require(pid);
        verified = reloaded is not null
            && reloaded.Materials.Select(m => m.Id).SequenceEqual(orig.Materials.Select(m => m.Id))
            && reloaded.Events.Select(e => e.Id).SequenceEqual(orig.Events.Select(e => e.Id))
            && reloaded.Branches.Select(b => b.Id).SequenceEqual(orig.Branches.Select(b => b.Id))
            && reloaded.Decisions.Select(d => d.Id).SequenceEqual(orig.Decisions.Select(d => d.Id));
    }
    return Results.Json(Wrap(new { report.Outcome, report.Message, verified, probeDb },
        verified ? null : "往返校验失败"), JsonOptions.Web);
});

app.Run();

public partial class Program { }
