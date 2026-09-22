using System.Text.Json;

namespace GeoChron.Core;

public sealed record DerivedPoint(long EventSeq, string InputSummary, int ParamsVersion, EngineOutput Output);

public sealed record ProjectState(
    CasePack Pack,
    ModelParams Params,
    IReadOnlyList<EventRecord> Events,
    IReadOnlyDictionary<long, DerivedPoint> Derivations,
    string? FrozenCandidateId,
    string Branch);

/// <summary>命令的领域结果类型，避免用异常表达预期分支。</summary>
public enum CommandErrorKind { FormatCorrupt, SemanticUnresolved, BudgetExhausted, Conflict, Validation }

public sealed record CommandError(CommandErrorKind Kind, IReadOnlyList<Diagnostic> Diagnostics, string Message);

public sealed record CommandResult(bool Ok, EventRecord? Event, EngineOutput? Output, CommandError? Error)
{
    public static CommandResult Success(EventRecord e, EngineOutput o) => new(true, e, o, null);
    public static CommandResult Fail(CommandError e) => new(false, null, null, e);
}

/// <summary>
/// 应用服务：业务规则的服务端入口，可被测试直接调用（不经过浏览器）。
/// 分层：不可变材料（导入/源数据变更只追加）与可修改决定（参数/人工决定）分离。
/// </summary>
public sealed class AppService(IEventStore store)
{
    public const string RuleVersion = "geochron-rules-2026.09.1";
    private readonly AgeModelEngine _engine = new();

    public IReadOnlyList<EventRecord> LoadEvents(string caseId, string? branch = null) =>
        store.ListEvents(caseId, branch);

    public ProjectState Replay(string caseId, string? branch = null, long? upToSeq = null)
    {
        var events = store.ListEvents(caseId, branch);
        CasePack? pack = null;
        ModelParams params_ = null!;
        var derivations = new Dictionary<long, DerivedPoint>();
        string? frozen = null;
        var br = branch ?? "main";
        foreach (var ev in events)
        {
            if (upToSeq is not null && ev.Seq > upToSeq) break;
            br = ev.Branch;
            switch (ev.Type)
            {
                case EventType.CaseImported:
                    pack = ev.Payload.Deserialize<CasePack>(CaseJson.Options)!;
                    break;
                case EventType.ParamsRevised:
                    params_ = ev.Payload.Deserialize<ModelParams>(CaseJson.Options)!;
                    break;
                case EventType.SourceDataChanged:
                {
                    var change = ev.Payload.Deserialize<SourceChangePayload>(CaseJson.Options)!;
                    pack = SourceMutator.Apply(pack!, change);
                    break;
                }
                case EventType.DecisionRecorded:
                {
                    var d = ev.Payload.Deserialize<DecisionPayload>(CaseJson.Options)!;
                    pack = SourceMutator.ApplyDecision(pack!, d);
                    break;
                }
                case EventType.CandidatesDerived:
                {
                    var output = ev.Payload.Deserialize<EngineOutput>(CaseJson.Options)!;
                    derivations[ev.Seq] = new DerivedPoint(ev.Seq, ev.InputSummary!, ev.ParamsVersion!.Value, output);
                    break;
                }
                case EventType.ModelFrozen:
                    frozen = ev.Payload.GetProperty("candidateId").GetString();
                    break;
                case EventType.NewAnchorCompared:
                {
                    var output = ev.Payload.GetProperty("output").Deserialize<EngineOutput>(CaseJson.Options)!;
                    derivations[ev.Seq] = new DerivedPoint(ev.Seq, ev.InputSummary!, ev.ParamsVersion!.Value, output);
                    break;
                }
                case EventType.ExperimentRecorded:
                    break;
            }
        }
        return new ProjectState(pack!, params_ ?? pack!.Params, events, derivations, frozen, br);
    }

    public CommandResult ImportCase(string caseJson, string actor = "sed-team", string caseIdOverride = "")
    {
        var (pack, diags) = AgeModelEngine.ParsePack(caseJson);
        if (pack is null)
            return CommandResult.Fail(new CommandError(CommandErrorKind.FormatCorrupt, diags, "案例包格式损坏"));
        if (diags.Any(d => d.Code == "P002"))
            return CommandResult.Fail(new CommandError(CommandErrorKind.FormatCorrupt, diags, "案例包 schema 不受支持"));
        var caseId = string.IsNullOrEmpty(caseIdOverride) ? pack.CaseId : caseIdOverride;
        var summary = AgeModelEngine.ComputeInputSummary(pack, pack.Params);
        var payload = JsonDocument.Parse(caseJson).RootElement.Clone();
        var importEv = Append(caseId, "main", null, EventType.CaseImported, actor,
            "初始导入：深度基准、分段缺口、放射性年龄分布、储层偏移、事件层相关与排除决定全部保留",
            payload, summary, pack.Params.Version,
            new[] { $"datum:{pack.DepthDatum.Id}" });
        var output = _engine.Run(pack);
        AppendDerived(importEv, output, $"按规则 {RuleVersion} 推导多候选深度-年龄模型谱系");
        return CommandResult.Success(importEv, output);
    }

    public CommandResult ReviseParams(string caseId, ModelParams next, string actor, string note)
    {
        var state = Replay(caseId);
        var current = state.Params;
        if (next.Version != current.Version + 1)
            return Fail(CommandErrorKind.Validation,
                new[] { new Diagnostic("V001", "params.version",
                    $"参数版本必须单调递增：当前 v{current.Version}，收到 v{next.Version}，期望 v{current.Version + 1}") },
                "参数版本冲突");
        if (next.McBudget <= 0 || next.GridStepCm <= 0)
            return Fail(CommandErrorKind.Validation,
                new[] { new Diagnostic("V002", "params", "mcBudget 与 gridStepCm 必须为正，禁止默认值伪装成功") },
                "参数非法");
        var pack = SourceMutator.WithParams(state.Pack, next);
        var summary = AgeModelEngine.ComputeInputSummary(pack, next);
        var ev = Append(caseId, state.Branch, LastSeq(state), EventType.ParamsRevised, actor,
            $"参数修订 v{current.Version}→v{next.Version}：{note}",
            JsonSerializer.SerializeToElement(next, CaseJson.Options), summary, next.Version,
            new[] { $"params:v{current.Version}" });
        var output = _engine.Run(pack, next);
        AppendDerived(ev, output, $"在新参数 v{next.Version} 下重算（父依赖 {summary}）");
        return CommandResult.Success(ev, output);
    }

    public CommandResult ChangeSourceData(string caseId, SourceChangePayload change, string actor, string note)
    {
        var state = Replay(caseId);
        var diagnostics = SourceMutator.Validate(state.Pack, change);
        if (diagnostics.Count > 0)
            return Fail(CommandErrorKind.Validation, diagnostics, "源数据改动被拒绝");
        var nextPack = SourceMutator.Apply(state.Pack, change);
        var summary = AgeModelEngine.ComputeInputSummary(nextPack, state.Params);
        var ev = Append(caseId, state.Branch, LastSeq(state), EventType.SourceDataChanged, actor,
            $"局部源数据改动：{note}（直接来源 {change.SubjectId}）",
            JsonSerializer.SerializeToElement(change, CaseJson.Options), summary, state.Params.Version,
            new[] { change.DependencyRef });
        var output = _engine.Run(nextPack, state.Params);
        AppendDerived(ev, output, "局部依赖重算：未涉及的层段候选保持同一输入摘要");
        return CommandResult.Success(ev, output);
    }

    public CommandResult RecordDecision(string caseId, DecisionPayload decision, string actor, string note)
    {
        var state = Replay(caseId);
        var nextPack = SourceMutator.ApplyDecision(state.Pack, decision);
        var summary = AgeModelEngine.ComputeInputSummary(nextPack, state.Params);
        var ev = Append(caseId, state.Branch, LastSeq(state), EventType.DecisionRecorded, actor,
            $"人工决定：{note}", JsonSerializer.SerializeToElement(decision, CaseJson.Options),
            summary, state.Params.Version, new[] { decision.SubjectRef });
        var output = _engine.Run(nextPack, state.Params);
        AppendDerived(ev, output, "人工决定后重算；与既有决定冲突时两者都保留为事件");
        return CommandResult.Success(ev, output);
    }

    public CommandResult FreezeModel(string caseId, string candidateId, string actor, string note)
    {
        var state = Replay(caseId);
        if (state.FrozenCandidateId is not null)
            return Fail(CommandErrorKind.Conflict,
                new[] { new Diagnostic("Z001", "freeze",
                    $"已冻结候选 {state.FrozenCandidateId}；并发/重复冻结必须返回冲突而不是覆盖") },
                "模型已冻结（并发冻结冲突）");
        var latest = state.Derivations.Values.OrderBy(d => d.EventSeq).LastOrDefault()
            ?? throw new InvalidOperationException("尚无可冻结的推导结果");
        if (latest.Output.Candidates.All(c => c.Id != candidateId))
            return Fail(CommandErrorKind.Validation,
                new[] { new Diagnostic("Z002", "candidateId", $"候选 {candidateId} 不存在于当前谱系") },
                "候选不存在");
        // 并发冻结：用条件插入检测竞争（两个事务只有一个能赢）。
        var payload = JsonSerializer.SerializeToElement(new { candidateId, note }, CaseJson.Options);
        try
        {
            var ev = store.AppendConditional(caseId, state.Branch, LastSeq(state),
                EventType.ModelFrozen, actor, $"冻结模型 {candidateId}：{note}", payload,
                latest.InputSummary, state.Params.Version, new[] { $"candidate:{candidateId}" }, RuleVersion);
            return CommandResult.Success(ev, latest.Output);
        }
        catch (ConcurrentAppendException)
        {
            return Fail(CommandErrorKind.Conflict,
                new[] { new Diagnostic("Z003", "freeze", "并发冻结竞争：另一事务已提交冻结，本操作被拒绝（冻结不可变）") },
                "并发冻结冲突");
        }
    }

    public CommandResult CompareNewAnchor(string caseId, Anchor anchor, string actor, string note)
    {
        var state = Replay(caseId);
        if (state.FrozenCandidateId is null)
            return Fail(CommandErrorKind.Validation,
                new[] { new Diagnostic("N001", "freeze", "需要先冻结一个模型，才能比较新锚点对冻结结果的影响") },
                "未冻结模型");
        if (!anchor.Depth.IsConvertible)
            return Fail(CommandErrorKind.Validation,
                new[] { new Diagnostic("N002", "anchor.depth",
                    $"新锚点深度不可转换：{anchor.Depth.Raw} {anchor.Depth.Unit}（{anchor.Depth.ConversionDiagnostic}）") },
                "新锚点无效");
        var frozenPack = state.Pack with
        {
            Anchors = state.Pack.Anchors.Append(anchor).ToList(),
            CaseId = state.Pack.CaseId
        };
        var output = _engine.Run(frozenPack, state.Params);
        var frozenCandidate = state.Derivations.Values.OrderBy(d => d.EventSeq).Last()
            .Output.Candidates.First(c => c.Id == state.FrozenCandidateId);
        var payload = JsonSerializer.SerializeToElement(new
        {
            anchor,
            frozenCandidateId = state.FrozenCandidateId,
            frozenInputSummary = frozenCandidate.InputSummary,
            output
        }, CaseJson.Options);
        var summary = AgeModelEngine.ComputeInputSummary(frozenPack, state.Params);
        var ev = Append(caseId, state.Branch, LastSeq(state), EventType.NewAnchorCompared, actor,
            $"新锚点 {anchor.Id} 与冻结模型 {state.FrozenCandidateId} 的影响比较：{note}",
            payload, summary, state.Params.Version,
            new[] { $"candidate:{state.FrozenCandidateId}", $"anchor:{anchor.Id}" });
        return CommandResult.Success(ev, output);
    }

    private EventRecord AppendDerived(EventRecord parent, EngineOutput output, string label)
    {
        var payload = JsonSerializer.SerializeToElement(output, CaseJson.Options);
        return Append(parent.CaseId, parent.Branch, parent.Seq, EventType.CandidatesDerived, "engine",
            label, payload, output.InputSummary, output.ParamsVersion,
            output.Candidates.SelectMany(c => c.ParentDependencies).Distinct().OrderBy(x => x).ToList());
    }

    private EventRecord Append(string caseId, string branch, long? parentSeq, EventType type, string actor,
        string label, JsonElement payload, string? summary, int? paramsVersion, IReadOnlyList<string> deps)
        => store.Append(caseId, branch, parentSeq, type, actor, label, payload, summary, paramsVersion, deps, RuleVersion);

    private static long? LastSeq(ProjectState s) => s.Events.Count == 0 ? null : s.Events.Max(e => e.Seq);

    private static CommandResult Fail(CommandErrorKind kind, IReadOnlyList<Diagnostic> diags, string msg) =>
        CommandResult.Fail(new CommandError(kind, diags, msg));
}

public sealed class ConcurrentAppendException : Exception;
