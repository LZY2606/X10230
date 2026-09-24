using System.Text.Json;

namespace DepthAge.Core;

public sealed record CaseMaterial(string Kind, JsonElement Payload);
public sealed record CaseStep(string Op, Dictionary<string, JsonElement>? Args, string? Note);
public sealed record CasePack(string Name, string Title, string Version, string Narrative,
    IReadOnlyList<CaseMaterial> Materials, IReadOnlyList<CaseStep> Script)
{
    public static CasePack Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var materials = root.GetProperty("materials").EnumerateArray()
            .Select(m => new CaseMaterial(m.GetProperty("kind").GetString()!, m.GetProperty("payload").Clone()))
            .ToList();
        var script = root.GetProperty("script").EnumerateArray()
            .Select(s => new CaseStep(
                s.GetProperty("op").GetString()!,
                s.TryGetProperty("args", out var a) ? a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : null,
                s.TryGetProperty("note", out var n) ? n.GetString() : null))
            .ToList();
        return new CasePack(
            root.GetProperty("name").GetString()!,
            root.GetProperty("title").GetString()!,
            root.GetProperty("version").GetString()!,
            root.GetProperty("narrative").GetString()!,
            materials, script);
    }
}

public sealed record ReplayStep(string Op, string Detail, IReadOnlyList<string> EventIds,
    IReadOnlyList<string> Flags, IReadOnlyList<string> Affected);

public sealed record ReplayReport(string CaseName, string Title,
    IReadOnlyList<ReplayStep> Steps, IReadOnlyList<string> Flags)
{
    public bool Covers(params string[] flags) => flags.All(f => Flags.Contains(f));
}

/// <summary>
/// 案例重放器：把离线案例包脚本逐步落到事件存储。
/// 每一步都留下输入片段、判定状态与受影响对象（机器可读事件），
/// 审阅者可以从最终状态反查材料、参数版本与决定。
/// </summary>
public static class CaseRunner
{
    private static readonly Dictionary<string, string> DiagFlagMap = new()
    {
        ["depth-duplicate"] = "depth-duplicate",
        ["parallel-anchors"] = "parallel-anchors",
        ["long-tail"] = "long-tail",
        ["inverted-segment"] = "inverted-segment",
        ["unconformity-boundary"] = "unconformity",
        ["gap-boundary"] = "gap",
    };

    public static ReplayReport Run(CasePack pack, DepthAgeStore store)
    {
        var steps = new List<ReplayStep>();
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        Dictionary<string, string>? previousSignature = null;
        string? lastDigest = null;
        var lastParams = new EngineParams();

        void CollectFlags(FitOutcome fit)
        {
            foreach (var d in fit.Diagnostics)
                if (DiagFlagMap.TryGetValue(d.Code, out var f)) flags.Add(f);
        }

        Dictionary<string, string> Signature(ProjectedState state)
        {
            var fit = state.Branches.First(b => b.Branch == DecisionPayload.MainBranch).Fit;
            return fit.Model?.Segments.ToDictionary(s => s.Segment,
                s => s.Status + ":" + string.Join(",", s.Candidates.Select(c => c.Id)))
                ?? new Dictionary<string, string>();
        }

        foreach (var step in pack.Script)
        {
            switch (step.Op)
            {
                case "import":
                {
                    var ids = new List<string>();
                    var ev = store.AppendEvent(ModelEvent.ImportKind,
                        JsonOptions.Serialize(new { note = step.Note ?? "initial import", count = pack.Materials.Count }),
                        "sha256:" + MaterialHashes.Sha256Hex(string.Join("|", pack.Materials.Select(m => m.Payload.GetRawText())))[..16],
                        lastParams.ParamVersion, Array.Empty<string>());
                    foreach (var m in pack.Materials)
                        ids.Add(store.AddMaterial(m.Kind, m.Payload.GetRawText(), ev.Seq).Id);
                    steps.Add(new ReplayStep("import", $"imported {ids.Count} immutable materials (raw representation preserved)",
                        new[] { ev.Id }, Array.Empty<string>(), Array.Empty<string>()));
                    break;
                }
                case "fit":
                {
                    var state = Projector.Project(store);
                    var fit = state.Branches.First(b => b.Branch == DecisionPayload.MainBranch).Fit;
                    CollectFlags(fit);
                    var affected = new List<string>();
                    var sig = Signature(state);
                    if (previousSignature is not null)
                        affected = sig.Where(kv => !previousSignature.TryGetValue(kv.Key, out var old) || old != kv.Value)
                                      .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (previousSignature is not null && affected.Count > 0 && affected.Count < sig.Count)
                        flags.Add("local-recompute");
                    previousSignature = sig;
                    lastDigest = fit.Model?.InputDigest;
                    lastParams = state.Params;
                    var payload = new
                    {
                        note = step.Note ?? "fit",
                        inputDigest = fit.Model?.InputDigest,
                        paramVersion = state.Params.ParamVersion,
                        status = fit.Status.ToString(),
                        segments = fit.Model?.Segments.Select(s => new
                        {
                            s.Segment, status = s.Status.ToString(),
                            candidates = s.Candidates.Select(c => c.Id).ToArray(),
                            rates = s.Rates.Select(r => r.ToString()).ToArray(),
                            diagnostics = s.Diagnostics.Select(d => d.Code).ToArray()
                        }).ToArray(),
                        affected
                    };
                    var parents = state.Materials.Select(m => m.Id).ToList();
                    var ev = store.AppendEvent(ModelEvent.FitKind, JsonOptions.Serialize(payload),
                        fit.Model?.InputDigest ?? "none", state.Params.ParamVersion, parents);
                    steps.Add(new ReplayStep("fit",
                        $"fit status={fit.Status} params={state.Params.ParamVersion} digest={fit.Model?.InputDigest}" +
                        (affected.Count > 0 ? $" affected=[{string.Join(",", affected)}]" : ""),
                        new[] { ev.Id }, fit.Diagnostics.Select(d => d.Code).Distinct().ToArray(), affected));
                    break;
                }
                case "revise-params":
                {
                    var args = step.Args!;
                    var payload = new ParamRevisionPayload(
                        args["paramVersion"].GetString()!,
                        args["defaultReservoirOffsetKa"].GetDouble(),
                        args["reason"].GetString()!,
                        args["by"].GetString()!);
                    var ev = store.AppendEvent(ModelEvent.ParamRevisionKind, JsonOptions.Serialize(payload),
                        lastDigest ?? "none", payload.ParamVersion, Array.Empty<string>());
                    steps.Add(new ReplayStep("revise-params",
                        $"parameter revision -> {payload.ParamVersion} (default reservoir offset {payload.DefaultReservoirOffsetKa} ka): {payload.Reason}",
                        new[] { ev.Id }, Array.Empty<string>(), Array.Empty<string>()));
                    break;
                }
                case "change-source":
                {
                    var mat = step.Args!["material"];
                    var kind = mat.GetProperty("kind").GetString()!;
                    var raw = mat.GetProperty("payload").GetRawText();
                    var scEv = store.AppendEvent(ModelEvent.SourceChangeKind,
                        JsonOptions.Serialize(new { note = step.Note ?? "single direct source changed", kind }),
                        lastDigest ?? "none", lastParams.ParamVersion, Array.Empty<string>());
                    var env = store.AddMaterial(kind, raw, scEv.Seq);
                    // 局部重算：只重新推导受影响层段
                    var state = Projector.Project(store);
                    var fit = state.Branches.First(b => b.Branch == DecisionPayload.MainBranch).Fit;
                    CollectFlags(fit);
                    var sig = Signature(state);
                    var affected = previousSignature is null
                        ? sig.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList()
                        : sig.Where(kv => !previousSignature.TryGetValue(kv.Key, out var old) || old != kv.Value)
                             .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
                    if (affected.Count > 0 && affected.Count < sig.Count) flags.Add("local-recompute");
                    previousSignature = sig;
                    lastDigest = fit.Model?.InputDigest;
                    var payload = new
                    {
                        note = "local recompute after source change",
                        inputDigest = fit.Model?.InputDigest,
                        paramVersion = state.Params.ParamVersion,
                        status = fit.Status.ToString(),
                        segments = fit.Model?.Segments.Select(s => new
                        {
                            s.Segment, status = s.Status.ToString(),
                            candidates = s.Candidates.Select(c => c.Id).ToArray(),
                            diagnostics = s.Diagnostics.Select(d => d.Code).ToArray()
                        }).ToArray(),
                        affected
                    };
                    var fitEv = store.AppendEvent(ModelEvent.FitKind, JsonOptions.Serialize(payload),
                        fit.Model?.InputDigest ?? "none", state.Params.ParamVersion,
                        state.Materials.Select(m => m.Id).ToList());
                    steps.Add(new ReplayStep("change-source",
                        $"source material {env.Id} added; recompute affected only [{string.Join(",", affected)}]",
                        new[] { scEv.Id, fitEv.Id },
                        fit.Diagnostics.Select(d => d.Code).Distinct().ToArray(), affected));
                    break;
                }
                case "decide":
                {
                    var args = step.Args!;
                    var payload = new DecisionPayload(
                        args["decision"].GetString()!, args["target"].GetString()!,
                        args["reason"].GetString()!, args["by"].GetString()!,
                        args.TryGetValue("branch", out var b) ? b.GetString() ?? DecisionPayload.MainBranch : DecisionPayload.MainBranch);
                    var ev = store.AppendEvent(ModelEvent.DecisionKind, JsonOptions.Serialize(payload),
                        lastDigest ?? "none", lastParams.ParamVersion, new[] { payload.Target });
                    steps.Add(new ReplayStep("decide",
                        $"{payload.By} decided {payload.Decision} on {payload.Target} (branch {payload.Branch}): {payload.Reason}",
                        new[] { ev.Id }, Array.Empty<string>(), new[] { payload.Target }));
                    break;
                }
                case "freeze":
                {
                    var args = step.Args ?? new Dictionary<string, JsonElement>();
                    var branch = args.TryGetValue("branch", out var b) ? b.GetString() ?? "main" : "main";
                    var by = args.TryGetValue("by", out var u) ? u.GetString() ?? "system" : "system";
                    var state = Projector.Project(store);
                    var fit = state.Branches.First(x => x.Branch == branch).Fit;
                    var digest = fit.Model?.InputDigest ?? "none";
                    var outcome = store.TryFreeze(digest, branch, by, store.MaxSeq() + 1);
                    if (outcome == FreezeOutcome.Frozen)
                    {
                        var payload = new FreezePayload(digest, branch, by, fit.Model!);
                        var ev = store.AppendEvent(ModelEvent.FreezeKind, JsonOptions.Serialize(payload),
                            digest, state.Params.ParamVersion, new[] { digest });
                        steps.Add(new ReplayStep("freeze", $"model {digest} frozen on branch {branch} by {by}",
                            new[] { ev.Id }, Array.Empty<string>(), Array.Empty<string>()));
                    }
                    else
                    {
                        var ev = store.AppendEvent(ModelEvent.FreezeConflictKind,
                            JsonOptions.Serialize(new { modelDigest = digest, branch, by, reason = "model already frozen; concurrent freeze rejected" }),
                            digest, state.Params.ParamVersion, new[] { digest });
                        flags.Add("concurrent-freeze");
                        steps.Add(new ReplayStep("freeze", $"concurrent freeze on {digest} rejected (already frozen)",
                            new[] { ev.Id }, new[] { "concurrent-freeze" }, Array.Empty<string>()));
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown case op '{step.Op}'");
            }
        }
        return new ReplayReport(pack.Name, pack.Title, steps, flags.ToList());
    }
}
