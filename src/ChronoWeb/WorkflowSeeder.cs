using ChronoCore;
using ChronoStorage;

namespace ChronoWeb;

/// <summary>一次性把完整叙事（导入→推导→复核→反例档案）种入空库。</summary>
public sealed class WorkflowSeeder
{
    private readonly ChronoRepository _repo;
    public WorkflowSeeder(ChronoRepository repo) { _repo = repo; }

    public SeedingSummary Seed()
    {
        var bundle = CaseBuilder.Build();
        var sink = new SqliteEventSink(_repo);
        var app = new ChronoAppService(sink);
        app.SeedFromCase(bundle);

        var events = _repo.ReadEvents(ChronoAppService.MainBranch);
        var state = EventFolder.FoldTo(events, ChronoAppService.MainBranch, null);
        var p1 = state.Parameters["v1"];
        var baselineDigest = app.Engine.ComputeInputDigest(state, p1);

        // step1：基线派生（并列锚点 → 多套等优候选）
        var d1 = app.DeriveAndRecord(ChronoAppService.MainBranch, state, p1);

        // step2：参数修订 v1→v2
        var p2 = state.Parameters["v2"];
        app.ReviseMaterial(ChronoAppService.MainBranch, "parameter-set", "params", p2 with { },
            events.Last(e => e.Type == EventType.ParameterRevised).Id, "full");
        events = _repo.ReadEvents(ChronoAppService.MainBranch);
        state = EventFolder.FoldTo(events, ChronoAppService.MainBranch, null);
        var d2 = app.DeriveAndRecord(ChronoAppService.MainBranch, state, p2);

        // step3：局部源数据改动 an-07（只重算 iv-2）
        var an07 = state.Anchors["an-07"];
        var an07new = an07 with
        {
            Samples = SamplesFactory.StudentT(172500, 4800, 7),
            Note = "重测版本：172.5 ka；局部重算只影响 iv-2"
        };
        app.ReviseMaterial(ChronoAppService.MainBranch, "anchor", "an-07", an07new,
            events.Last(e => e.Type == EventType.MaterialImported && e.Id.Contains("material-imported")).Id,
            "local:iv-2");
        events = _repo.ReadEvents(ChronoAppService.MainBranch);
        state = EventFolder.FoldTo(events, ChronoAppService.MainBranch, null);
        var d3 = app.DeriveAndRecord(ChronoAppService.MainBranch, state, p2,
            localOnly: new[] { "iv-2" });

        // step4：互相冲突的人工决定（v2 接受 vs v3 关闭储层校正），两条决定都保留
        var bestV2 = d3.Candidates.OrderBy(m => m.FitScore).First(m => m.SemanticallyValid);
        app.RecordDecision(ChronoAppService.MainBranch,
            new Decision("dec-accept-v2", "accept", bestV2.Id, "v2",
                "接受 v2（应用局地储层校正 ΔR=320±75）的最优候选", "reviewer-A", 1),
            false, null);
        var p3 = state.Parameters["v3"];
        app.ReviseMaterial(ChronoAppService.MainBranch, "parameter-set", "params-v3", p3,
            events.Last(e => e.Type == EventType.ParameterRevised).Id, "full");
        events = _repo.ReadEvents(ChronoAppService.MainBranch);
        var stateV3 = EventFolder.FoldTo(events, ChronoAppService.MainBranch, null);
        var d3alt = app.DeriveAndRecord(ChronoAppService.MainBranch, stateV3, p3, actor: "reviewer-B");
        var bestV3 = d3alt.Candidates.OrderBy(m => m.FitScore).FirstOrDefault(m => m.SemanticallyValid);
        app.RecordDecision(ChronoAppService.MainBranch,
            new Decision("dec-accept-v3", "accept", bestV3?.Id ?? "none", "v3",
                "冲突决定：关闭储层校正的等优谱系也保留，不由系统二选一", "reviewer-B", 2),
            true, "dec-accept-v2");

        // step5：冻结 v2 模型（reviewer-A）
        events = _repo.ReadEvents(ChronoAppService.MainBranch);
        state = EventFolder.FoldTo(events, ChronoAppService.MainBranch, null);
        var frozen = app.Engine.Derive(state, p2).Candidates
            .Where(m => m.SemanticallyValid).OrderBy(m => m.FitScore).First();
        // 用 d3 已记录模型 Id（输入相同 → 内容寻址一致）
        app.TryFreeze(ChronoAppService.MainBranch, frozen.Id, frozen.InputDigest, "v2",
            "reviewer-A", out _, out _);

        // 反例档案：基线摘要 + 一次只改一个直接来源的分支
        var runner = new ReplayRunner(app);
        var replayResults = new List<ReplayStepResult>();
        var baselineState = EventFolder.FoldTo(_repo.ReadEvents(ChronoAppService.MainBranch),
            ChronoAppService.MainBranch, null);
        var branchNames = new Dictionary<string, string>
        {
            {"depth-duplicate","counter-depth-duplicate"},
            {"unconformity","counter-unconformity"},
            {"inverted-section","counter-inverted"},
            {"tie-anchor","counter-tie-anchor"},
            {"long-tail","counter-long-tail"},
            {"local-recompute","counter-local-recompute"},
            {"concurrent-freeze","counter-concurrent-freeze"}
        };
        foreach (var sc in bundle.ReplayScenarios)
        {
            var bn = branchNames[sc.Scenario];
            _repo.ForkBranch(bn, ChronoAppService.MainBranch, events.Count,
                ChronoAppService.ProjectId, baselineDigest,
                DirectSource(sc), $"反例：{sc.Title}");
            app.ForkBranch(bn, ChronoAppService.MainBranch, events.Count,
                baselineDigest, DirectSource(sc), sc.Title);
            var bs = EventFolder.FoldTo(_repo.ReadAllEvents(), bn, null);
            var result = runner.Run(sc, bs, p2, bn, events.Count, baselineDigest);
            replayResults.Add(result);
        }

        return new SeedingSummary(bundle, baselineDigest, d1, d2, d3, d3alt, frozen, replayResults);
    }

    private static string DirectSource(ReplayScenario sc) => sc.Scenario switch
    {
        "depth-duplicate" => "anchor:an-x-dup",
        "unconformity" => "anchor:an-x-unc",
        "inverted-section" => "anchor:an-x-inv",
        "tie-anchor" => "parameter:maxCandidates",
        "long-tail" => "anchor:an-04",
        "local-recompute" => "anchor:an-07",
        "concurrent-freeze" => "freeze:" + sc.Scenario,
        _ => "unknown"
    };
}

public sealed record SeedingSummary(
    CaseBundle Bundle,
    string BaselineDigest,
    DeriveOutcome DeriveV1,
    DeriveOutcome DeriveV2,
    DeriveOutcome DeriveLocal,
    DeriveOutcome DeriveV3,
    DerivedModel Frozen,
    IReadOnlyList<ReplayStepResult> ReplayResults);
