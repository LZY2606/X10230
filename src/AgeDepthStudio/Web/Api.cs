using System.Text.Json;
using AgeDepthStudio.CasePackages;
using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;
using AgeDepthStudio.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AgeDepthStudio.Web;

public static class Api
{
    public static void Map(WebApplication app, StudioService svc, string dbPath, Func<string, string> packagePath)
    {
        app.UseStaticFiles();

        app.MapGet("/api/workspaces", () =>
            svc.Store.Workspaces().Select(w => new { w.Id, w.Name, w.Kind }));

        app.MapGet("/api/workspaces/{ws}/verify", (string ws) =>
            Results.Ok(new { valid = svc.Store.VerifyChain(ws), head = svc.Store.HeadHash(ws) }));

        app.MapGet("/api/workspaces/{ws}", (string ws, long? seq) =>
        {
            if (!svc.Store.WorkspaceExists(ws)) return Results.NotFound(new { error = "workspace not found" });
            var events = svc.Store.Events(ws);
            var state = Projector.Project(seq is null ? events : events.Take((int)seq.Value).ToList());
            var current = Projector.Project(events);
            var model = state.Models.LastOrDefault();
            return Results.Ok(new
            {
                state = StateDto(state),
                events = events.Select(e => new { e.Seq, e.Id, e.Type, e.At, e.Actor, payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson) }),
                model = model,
                figureUrl = $"/api/workspaces/{ws}/figure" + (seq is null ? "" : $"?seq={seq}"),
                currentHeadSeq = events.Count == 0 ? 0 : events[^1].Seq,
                viewingSeq = seq ?? (events.Count == 0 ? 0 : events[^1].Seq),
                chainValid = svc.Store.VerifyChain(ws),
                frozen = current.Freeze
            });
        });

        app.MapGet("/api/workspaces/{ws}/figure", (string ws, long? seq) =>
        {
            var events = svc.Store.Events(ws);
            var state = Projector.Project(seq is null ? events : events.Take((int)seq.Value).ToList());
            var model = state.Models.LastOrDefault();
            if (model is null) return Results.NotFound(new { error = "尚无模型，请先重算" });
            return Results.Content(SvgRenderer.Render(state, model), "image/svg+xml");
        });

        app.MapPost("/api/workspaces/{ws}/recompute", (string ws) =>
        {
            var e = svc.Recompute(ws, svc.NextModelId(ws));
            return Results.Ok(new { e.Id, e.Seq });
        });

        app.MapPost("/api/workspaces/{ws}/freeze", (string ws, FreezeRequest req) =>
        {
            var state = svc.State(ws);
            var modelId = req.ModelId ?? state.Models.LastOrDefault()?.ModelId
                ?? throw new BadHttpRequestException("no model to freeze");
            var ok = svc.TryFreeze(ws, modelId, req.ExpectedParamsId ?? state.HeadParamsId, req.Actor ?? "reviewer", out var conflict);
            return Results.Ok(new { ok, conflict = conflict is null ? null : new { conflict.Id, conflict.Seq, conflict.Type } });
        });

        app.MapPost("/api/workspaces/{ws}/trial", (string ws, TrialHttpRequest req) =>
        {
            var o = new TrialOverride(req.Override?.Kind ?? "anchor-add", req.Override?.Id ?? "A-UI",
                req.Override?.DepthRaw, req.Override?.Samples, req.Override?.SegmentId, req.Override?.Budget);
            var request = new TrialRequest(req.TrialId ?? $"T-{Guid.NewGuid():N}".Substring(0, 10),
                req.DirectSourceId ?? o.Id, req.Mutation ?? "界面试验", o);
            var t = svc.RunTrial(ws, request, req.Adopt, req.Actor ?? "reviewer");
            return Results.Ok(new { t.TrialId, t.Outcome, t.AffectedSegmentIds, t.ImpactScope, t.Adopted });
        });

        app.MapPost("/api/workspaces/{ws}/decisions", (string ws, DecisionHttpRequest req) =>
        {
            var d = new Decision(req.DecisionId ?? $"DEC-{Guid.NewGuid():N}".Substring(0, 9),
                req.TargetId!, Enum.Parse<DecisionAction>(req.Action!), req.Rationale ?? "",
                req.Author ?? "reviewer", DateTimeOffset.UtcNow.ToString("o"));
            var e = svc.RecordDecision(ws, d, d.Author);
            return Results.Ok(new { e.Id });
        });

        app.MapGet("/api/workspaces/{ws}/export", (string ws) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(svc.Store.ExportArchive(ws)),
                "application/json", $"{ws}-archive.json"));

        app.MapPost("/api/import", async (HttpRequest http) =>
        {
            using var reader = new StreamReader(http.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                svc.Store.ImportArchive(json);
                return Results.Ok(new { ok = true });
            }
            catch (JsonException ex)
            {
                // 格式损坏：独立结果类别，带位置。
                return Results.BadRequest(new { outcome = "FormatCorrupt", error = ex.Message, position = (ex.LineNumber, ex.BytePositionInLine) });
            }
            catch (InvalidDataException ex)
            {
                return Results.UnprocessableEntity(new { outcome = "SemanticUnsolvable", error = ex.Message });
            }
        });


        app.MapPost("/api/admin/reset-db", () =>
        {
            svc.Store.Reset();
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var file in Directory.GetFiles(packagePath(""), "*.json").OrderBy(f => f))
            {
                var pkg = JsonSerializer.Deserialize<CasePackage>(File.ReadAllText(file), jsonOpts)!;
                Replayer.Replay(svc, pkg);
            }
            return Results.Ok(new { ok = true, reseeded = true });
        });

        app.MapGet("/api/case-packages", () =>
        {
            var dir = packagePath("");
            return Directory.GetFiles(dir, "*.json").Select(Path.GetFileName);
        });
    }

    public sealed record FreezeRequest(string? ModelId, string? ExpectedParamsId, string? Actor);
    public sealed record TrialHttpRequest(string? TrialId, string? DirectSourceId, string? Mutation,
        bool Adopt, string? Actor, OverrideBody? Override);
    public sealed record OverrideBody(string? Kind, string? Id, string? DepthRaw, double[]? Samples,
        string? SegmentId, int? Budget);
    public sealed record DecisionHttpRequest(string? DecisionId, string? TargetId, string? Action,
        string? Rationale, string? Author);

    private static object StateDto(ProjectedState s) => new
    {
        project = s.Project,
        segments = s.Segments,
        gaps = s.Gaps,
        anchors = s.Anchors,
        offsets = s.Offsets,
        layers = s.Layers,
        parameters = s.Params,
        activeParamsId = s.ActiveParamsId,
        decisions = s.Decisions,
        models = s.Models,
        trials = s.Trials,
        freeze = s.Freeze,
        baselineModelId = s.BaselineModelId
    };
}
