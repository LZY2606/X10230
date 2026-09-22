using System.Reflection;

namespace GeoChron.Core;

/// <summary>离线案例包：直接随程序分发，不依赖云端或通用表格。</summary>
public static class SampleCase
{
    public const string CaseId = "core-x07-2026";

    public static string LoadJson()
    {
        var asm = typeof(SampleCase).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("sample-case.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// 完整叙事重放（不是彼此无关的按钮）：
/// 初始导入 → 参数修订 → 只影响局部依赖的源数据改动 → 互相冲突的人工决定。
/// 边界组合（深度重复/倒置/并列/长尾/局部重算/并发冻结）由反例档案与会话操作触发。
/// </summary>
public static class CaseNarrative
{
    public sealed record WalkthroughResult(
        EventRecord Import, EventRecord Params, EventRecord SourceChange, EventRecord ConflictingDecision,
        EngineOutput Final, IReadOnlyList<ExperimentRecord> Experiments);

    public static WalkthroughResult Play(AppService app, ExperimentArchive archive)
    {
        var caseId = SampleCase.CaseId;
        var importResult = app.ImportCase(SampleCase.LoadJson(), "sed-team");
        if (!importResult.Ok)
            throw new InvalidOperationException("样例导入失败：" + importResult.Error!.Message);

        var state = app.Replay(caseId);
        var revised = state.Params with
        {
            Version = 2,
            McBudget = 800,
            GridStepCm = 2.0,
            TieToleranceKa = 0.08
        };
        var r2 = app.ReviseParams(caseId, revised, "sed-team",
            "提高蒙特卡洛预算并加密网格，尾部分位数保持 0.025/0.5/0.975");
        if (!r2.Ok) throw new InvalidOperationException("参数修订失败：" + r2.Error!.Message);

        // 一次只改一个直接来源：把 anc-03 深度从 78cm 微调到 79cm（仅影响 B 段相关候选）。
        var change = new SourceChangePayload("chg-walk-01", "anchor", "anc-03", "depth",
            "78", "79", "cm", "现场复测深度尺读数偏移 1cm");
        var r3 = app.ChangeSourceData(caseId, change, "sed-team",
            "只影响 anc-03 所在 B 段的局部重算");
        if (!r3.Ok) throw new InvalidOperationException("局部源数据改动失败：" + r3.Error!.Message);

        // 互相冲突的人工决定：初始排除 anc-04，随后审阅者又接受它；两条决定都保留为事件。
        var reject = new DecisionPayload("dec-reject-anc04", "anchor", "anc-04", "reject", null,
            "维持初始排除：壳片疑似再搬运");
        var rr = app.RecordDecision(caseId, reject, "reviewer-a", "否定 anc-04");
        if (!rr.Ok) throw new InvalidOperationException("排除决定失败：" + rr.Error!.Message);
        var accept = new DecisionPayload("dec-accept-anc04", "anchor", "anc-04", "accept", null,
            "新照片证据表明壳片为原地埋藏；与上一决定冲突，两者都保留在谱系");
        var ra = app.RecordDecision(caseId, accept, "reviewer-b", "冲突决定：翻案接受 anc-04");
        if (!ra.Ok) throw new InvalidOperationException("接受决定失败：" + ra.Error!.Message);

        // 反例档案：倒置区间（未采纳）与长尾（采纳），采纳与否都保留。
        var exInv = archive.RunScenario(caseId, "inverted-interval", "anc-02", false);
        var exTail = archive.RunScenario(caseId, "long-tail", "resv-nerpacific", true);

        var finalState = app.Replay(caseId);
        var finalOutput = finalState.Derivations.Values.OrderBy(d => d.EventSeq).Last().Output;
        return new WalkthroughResult(importResult.Event!, r2.Event!, r3.Event!, ra.Event!, finalOutput,
            new[] { exInv.Record, exTail.Record });
    }
}
