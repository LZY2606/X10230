using System.Text.Json;
using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;
using Chrono.Storage;
using Chrono.Storage.Bundle;
using Chrono.Storage.CasePack;
using Chrono.Storage.Rendering;
using Chrono.Storage.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace Chrono.Web.Api;

public static class ApiEndpoints
{
    public static void Map(WebApplication app, ProjectStore store, JsonSerializerOptions json)
    {
        ReviewModel GetReview(string branch, string? atEvent)
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var snap = workflow.Snapshot(branch, atEvent)
                       ?? throw new InvalidOperationException("no materials at this point in history");
            return ReviewModelBuilder.Build(snap, branch, store.ListBranches(), store.IsFrozen(branch));
        }

        app.MapGet("/api/state", (string? branch, string? atEvent) =>
            Results.Json(GetReview(branch ?? "main", atEvent), json));

        app.MapGet("/api/svg/profile", (string? branch, string? atEvent) =>
            Results.Content(SvgProfile.Render(GetReview(branch ?? "main", atEvent)),
                "image/svg+xml"));
        app.MapGet("/api/svg/anchors", (string? branch, string? atEvent) =>
            Results.Content(SvgAnchors.Render(GetReview(branch ?? "main", atEvent)),
                "image/svg+xml"));
        app.MapGet("/api/svg/bands", (string? branch, string? atEvent) =>
            Results.Content(SvgBands.Render(GetReview(branch ?? "main", atEvent)),
                "image/svg+xml"));

        app.MapGet("/api/branches", () => Results.Json(new
        {
            projectId = store.GetProjectId(),
            branches = store.ListBranches()
        }, json));

        app.MapPost("/api/branch", ([FromBody] BranchRequest req) =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var events = store.ReadEvents(req.FromBranch ?? "main");
            var head = req.AtEventId ?? events.Last().Id;
            var id = string.IsNullOrWhiteSpace(req.BranchId)
                ? "review-" + Guid.NewGuid().ToString("N")[..8]
                : req.BranchId;
            var eventId = workflow.Branch(store.GetProjectId(), id, head, req.Reason ?? "review branch",
                req.Actor ?? "reviewer");
            return Results.Json(new { branchId = id, eventId }, json);
        });

        app.MapPost("/api/decision", ([FromBody] DecisionApiRequest req) =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var events = store.ReadEvents(req.Branch ?? "main");
            var head = req.ParentEventId ?? events.Last().Id;
            if (!Enum.TryParse<DecisionAction>(req.Action, true, out var action))
                return Results.BadRequest(new { error = "unknown action", allowed = Enum.GetNames<DecisionAction>() });
            var payload = new DecisionPayload(
                "dec-" + req.Action.ToLowerInvariant() + "-" + req.TargetId,
                action, req.TargetId, req.Rationale ?? "", null, null);
            var result = workflow.RecordDecision(store.GetProjectId(), req.Branch ?? "main",
                payload, head, req.Actor ?? "reviewer");
            if (!result.Ok)
                return Results.Json(new { failure = result.Failure.ToString() }, statusCode: 409);
            var recompute = workflow.Compute(store.GetProjectId(), req.Branch ?? "main",
                BuildBudget.Default, req.Actor ?? "reviewer",
                $"recompute after {action} on {req.TargetId}");
            return Results.Json(new
            {
                eventId = result.Event!.Id,
                recomputeEventId = recompute.Event?.Id,
                buildStatus = recompute.Build?.Status.ToString(),
                summary = recompute.Build?.Report.Summary
            }, json);
        });

        app.MapPost("/api/freeze", ([FromBody] FreezeRequest req) =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var head = store.ReadEvents(req.Branch ?? "main").Last();
            var result = workflow.Freeze(store.GetProjectId(), req.Branch ?? "main",
                req.CandidateId, head.Id, null, req.Reason ?? "frozen for comparison",
                req.Actor ?? "reviewer");
            if (!result.Ok)
                return Results.Json(new { failure = result.Failure.ToString() }, statusCode: 409);
            return Results.Json(new { eventId = result.Event!.Id, frozen = true }, json);
        });

        app.MapPost("/api/parameters", ([FromBody] ParametersRequest req) =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var head = store.ReadEvents(req.Branch ?? "main").Last();
            var parameters = new ChronoParameters(
                req.Version ?? "params-custom",
                req.MonteCarloSamples ?? 2000,
                req.RandomSeed ?? 20260923L,
                req.MinRateCmPerKa ?? 0,
                req.MaxRateCmPerKa ?? double.PositiveInfinity,
                req.GapPenaltyWeight ?? 1.0,
                req.BasisNote ?? "reviewer-adjusted parameters");
            var result = workflow.ReviseParameters(store.GetProjectId(), req.Branch ?? "main",
                parameters, head.Id, req.Actor ?? "reviewer",
                "reviewer parameter adjustment");
            if (!result.Ok)
                return Results.Json(new { failure = result.Failure.ToString() }, statusCode: 409);
            var recompute = workflow.Compute(store.GetProjectId(), req.Branch ?? "main",
                BuildBudget.Default, req.Actor ?? "reviewer", "recompute after parameter adjustment");
            return Results.Json(new
            {
                eventId = result.Event!.Id,
                parametersVersion = parameters.Version,
                buildStatus = recompute.Build?.Status.ToString()
            }, json);
        });

        app.MapPost("/api/probe-anchor", ([FromBody] ProbeAnchorRequest req) =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var baseline = workflow.StateAt("main");
            if (baseline.Materials == null)
                return Results.Json(new { failure = "no-materials" }, statusCode: 409);

            var datum = VerticalDatum.Csfa;
            var parse = RawParser.ParseDepth("probe", "depth", req.DepthRaw, datum);
            if (!parse.IsUsable)
                return Results.Json(new
                {
                    failure = parse.Token.Status.ToString(),
                    diagnostic = parse.Token.Diagnostic,
                    position = "probe#depth",
                    rawText = req.DepthRaw
                }, statusCode: 422);

            if (!double.TryParse(req.MedianCalBp, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var median) || median <= 0)
                return Results.Json(new
                {
                    failure = "Unconverted",
                    diagnostic = "median cal BP is not a positive number; value left at source position",
                    position = "probe#age",
                    rawText = req.MedianCalBp
                }, statusCode: 422);

            double sigma = req.SigmaCalYr ?? 80;
            AgeDistribution dist = sigma <= 0
                ? new PointDistribution(median)
                : new NormalDistribution(median, sigma);

            var branchId = "probe-" + Guid.NewGuid().ToString("N")[..8];
            var frozenHead = store.ReadEvents("main").Last();
            workflow.Branch(store.GetProjectId(), branchId, frozenHead.Id,
                "probe: compare effect of proposed new anchor against frozen main",
                req.Actor ?? "reviewer");

            var tempId = "probe-anchor-" + Guid.NewGuid().ToString("N")[..8];
            var probeAnchor = new Anchor(tempId,
                "PROPOSED " + (req.Label ?? tempId), AnchorRole.Radiocarbon,
                parse.Value!.Value, req.DepthRaw ?? "", dist, null, null, null, null, null,
                new RawRadiometric("PROPOSED", req.Material ?? "unspecified",
                    req.ConventionalBpText ?? "", "", null, null),
                "reviewer-proposal", baseline.Materials.Anchors.Count + 1);
            var withProbe = baseline.Materials with
            {
                Anchors = baseline.Materials.Anchors.Append(probeAnchor).ToList(),
                RawTokens = baseline.Materials.RawTokens
                    .Append(new RawToken("probe", "depth", req.DepthRaw ?? "",
                        RawParseStatus.Parsed, parse.Token.CanonicalUnit, null)).ToList()
            };

            var commit = workflow.CommitMaterials(store.GetProjectId(), branchId, withProbe,
                baseline.Events.Count(e => e.Kind == EventKind.MaterialsCommitted) + 1,
                frozenHead.Id, "probe", req.Actor ?? "reviewer",
                new[] { tempId }, "proposed new anchor for frozen-model comparison");
            var probeBuild = workflow.Compute(store.GetProjectId(), branchId,
                BuildBudget.Default, req.Actor ?? "reviewer", "probe derivation");
            var baseBuild = workflow.Snapshot("main", null)!.Build!;

            return Results.Json(new
            {
                branchId,
                anchorId = tempId,
                commitEventId = commit.Event!.Id,
                probeStatus = probeBuild.Build!.Status.ToString(),
                probeSummary = probeBuild.Build.Report.Summary,
                diagnostics = probeBuild.Build.Report.Diagnostics,
                frozenMainCandidates = baseBuild.Candidates
                    .OrderBy(c => c.Rank).Take(4)
                    .Select(c => new
                    {
                        c.CandidateId, c.Rank, c.Score, c.CoveragePct,
                        medianHead = c.Band.Count > 0 ? c.Band[0].Q500CalBp : 0,
                        medianFoot = c.Band.Count > 0 ? c.Band[^1].Q500CalBp : 0
                    }),
                probeCandidates = probeBuild.Build.Candidates
                    .OrderBy(c => c.Rank).Take(4)
                    .Select(c => new
                    {
                        c.CandidateId, c.ChainId, c.Rank, c.Score, c.CoveragePct,
                        medianHead = c.Band.Count > 0 ? c.Band[0].Q500CalBp : 0,
                        medianFoot = c.Band.Count > 0 ? c.Band[^1].Q500CalBp : 0
                    })
            }, json);
        });

        app.MapPost("/api/export", () =>
        {
            var workflow = app.Services.GetRequiredService<WorkflowService>();
            var outPath = Path.Combine(Path.GetTempPath(),
                "chrono-export-" + Guid.NewGuid().ToString("N") + ".chrono.zip");
            BundleService.Export(store, store.GetProjectId(), CasePackBuilder.ProjectName,
                workflow.RuleSetVersion, outPath);
            return Results.File(outPath, "application/zip",
                "kc21-chrono-bundle.chrono.zip");
        });

        app.MapPost("/api/import", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
                return Results.Json(new { failure = "no file" }, statusCode: 400);
            var file = request.Form.Files[0];
            var temp = Path.Combine(Path.GetTempPath(),
                "chrono-import-" + Guid.NewGuid().ToString("N") + ".zip");
            await using (var fs = File.Create(temp))
                await file.CopyToAsync(fs);
            var importId = "imp-" + Guid.NewGuid().ToString("N");
            var result = BundleService.Import(store, temp, importId);
            return Results.Json(new
            {
                status = result.Status.ToString(),
                error = result.Error,
                eventCount = result.EventCount,
                importId,
                recoveryState = BundleService.ImportState(store, importId)
            },
            statusCode: result.Status == BundleStatus.Ok ? 200 : 422);
        });

        app.MapGet("/api/replay-report", () =>
            Results.Json(app.Services.GetRequiredService<ReplayReportHolder>().LastReport, json));
    }
}

public sealed record BranchRequest(string? BranchId, string? FromBranch, string? AtEventId,
    string? Reason, string? Actor);

public sealed record DecisionApiRequest(string? Branch, string? ParentEventId, string Action,
    string TargetId, string? Rationale, string? Actor);

public sealed record FreezeRequest(string? Branch, string CandidateId, string? Reason, string? Actor);

public sealed record ParametersRequest(string? Branch, string? Version, int? MonteCarloSamples,
    long? RandomSeed, double? MinRateCmPerKa, double? MaxRateCmPerKa,
    double? GapPenaltyWeight, string? BasisNote, string? Actor);

public sealed record ProbeAnchorRequest(string? DepthRaw, string? MedianCalBp, double? SigmaCalYr,
    string? Label, string? Material, string? ConventionalBpText, string? Actor);
