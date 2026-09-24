using AgeDepthStudio.CasePackages;
using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;
using Xunit;

namespace AgeDepthStudio.Tests.CasePackage;

public class CasePackageReplayTests
{
    private static (StudioService svc, CasePackage pkg, EventStore store) ReplayFile(string file)
    {
        var pkg = TestSupport.LoadEmbedded(file);
        var (svc, store) = TestSupport.New();
        Replayer.Replay(svc, pkg);
        return (svc, pkg, store);
    }

    [Fact]
    public void Narrative_package_seeds_review_ready_state()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat.json");
        var s = svc.State("A-LINE");
        Assert.NotNull(s.Project);
        Assert.Equal(4, s.Segments.Count);
        Assert.Equal(2, s.Gaps.Count);
        Assert.Contains(s.Gaps, g => g.Kind == GapKind.Unconformity);
        Assert.NotEmpty(s.Offsets);
        Assert.NotEmpty(s.Layers);
        Assert.NotEmpty(s.Decisions);
        Assert.NotEmpty(s.Models);
        Assert.NotNull(s.Freeze);
        Assert.True(store.VerifyChain("A-LINE"));
        // 无法转换的锚点仍在原位
        var bad = s.Anchors.Single(a => a.AnchorId == "A9");
        Assert.Equal("未测深 cm", bad.DepthRaw);
    }

    [Fact]
    public void Narrative_records_parameter_revision_local_change_conflict_and_frozen_trial()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat.json");
        var types = store.Events("A-LINE").Select(e => e.Type).ToHashSet();
        Assert.Contains("params.activated", types);
        Assert.Contains("anchor.revised", types);
        Assert.Contains("decision.conflict", types);
        Assert.Contains("model.frozen", types);
        Assert.Contains("trial.result", types);
        var trials = svc.State("A-LINE").Trials;
        Assert.All(trials, t => Assert.False(t.Adopted));
    }

    [Fact]
    public void Boundary_package_replays_all_seven_required_situations()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat-boundary.json");
        var s = svc.State("B-BND");
        var byId = s.Trials.ToDictionary(t => t.TrialId);
        // 深度重复
        Assert.Equal("Local", byId["T-DUP"].ImpactScope);
        // 不整合改判限制在边界相邻段
        var unc = byId["T-UNC"];
        Assert.Equal("Local", unc.ImpactScope);
        Assert.Equal(2, unc.AffectedSegmentIds.Count);
        // 倒置层段 -> 语义无解
        Assert.Equal(OutcomeKind.SemanticUnsolvable, byId["T-INV"].Outcome);
        // 并列锚点 -> 多候选保留（基线 BA5/BA6 已产生等优候选）
        var baseline = s.Models.First();
        var seg2 = baseline.Segments.Single(r => r.SegmentId == "BSEG-2");
        Assert.True(seg2.Candidates.Count >= 2);
        Assert.Equal(OutcomeKind.Ok, byId["T-TIE"].Outcome);
        // 长尾
        var judgments = store.Events("B-BND")
            .Where(e => e.Type == "trial.result")
            .Select(e => e.PayloadJson)
            .Any(p => p.Contains("judgment.long_tail"));
        Assert.True(judgments);
        // 局部重算（采纳）
        Assert.True(byId["T-LOCAL"].Adopted);
        Assert.Equal(new[] { "BSEG-2" }, byId["T-LOCAL"].AffectedSegmentIds);
        // 并发冻结：一个冲突 + 一次成功
        var freezes = store.Events("B-BND");
        Assert.Contains(freezes, e => e.Type == "freeze.conflict");
        Assert.Contains(freezes, e => e.Type == "model.frozen");
        // 预算耗尽
        Assert.Equal(OutcomeKind.BudgetExhausted, byId["T-BUD"].Outcome);
        // 格式损坏
        Assert.Equal(OutcomeKind.FormatCorrupt, byId["T-FMT"].Outcome);
    }

    [Fact]
    public void Boundary_archive_keeps_adopted_and_rejected_results()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat-boundary.json");
        var trials = svc.State("B-BND").Trials;
        Assert.Contains(trials, t => t.Adopted);
        Assert.Contains(trials, !t.Adopted);
        var adoptEvents = store.Events("B-BND").Where(e => e.Type == "trial.adoption").ToList();
        Assert.Contains(adoptEvents, e => e.PayloadJson.Contains("\"adopted\":true"));
        Assert.Contains(adoptEvents, e => e.PayloadJson.Contains("\"adopted\":false"));
    }

    [Fact]
    public void Provenance_links_from_final_state_back_to_material_rule_and_decision()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat-boundary.json");
        var rec = store.Events("B-BND").First(e => e.Type == "model.recomputed");
        using var doc = System.Text.Json.JsonDocument.Parse(rec.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("BPAR-1", root.GetProperty("paramsId").GetString());
        Assert.True(root.GetProperty("inputSummary").GetProperty("decisionIds").GetArrayLength() >= 1);
        Assert.Equal("bm1", root.GetProperty("triggerEventId").GetString());
        // 每个段结果可追溯到候选 -> 锚点
        var seg = root.GetProperty("result").GetProperty("segments")[1];
        Assert.NotEqual(0, seg.GetProperty("candidates").GetArrayLength());
    }

    [Fact]
    public void Units_and_datum_are_preserved_raw_on_import_normalized_only_when_derived()
    {
        var (svc, pkg, store) = ReplayFile("core-chronostrat.json");
        var s = svc.State("A-LINE");
        var a3 = s.Anchors.Single(a => a.AnchorId == "A3");
        // 修订后的原始表示以 cm 给出，必须保留
        Assert.Equal("740cm", a3.DepthRaw);
        var m = s.Models.Last();
        // 派生结是米
        var knots = m.Segments.Single(r => r.SegmentId == "SEG-2").Candidates[0].Knots;
        Assert.Contains(knots, k => k.DepthM is >= 7.39 and <= 7.41);
    }
}
