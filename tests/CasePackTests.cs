using DepthAge.Core;
using Xunit;

namespace DepthAge.Tests;

public class CasePackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "depthage-tests-" + Guid.NewGuid().ToString("N"));
    public CasePackTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (ReplayReport Report, DepthAgeStore Store) Replay()
    {
        var pack = CasePack.Load(File.ReadAllText(RoundTripTests.CasePath()));
        var store = new DepthAgeStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        return (CaseRunner.Run(pack, store), store);
    }

    [Fact]
    public void Replay_CoversAllBoundaryCases()
    {
        var (report, store) = Replay();
        using (store)
        {
            Assert.True(report.Covers(
                "depth-duplicate", "unconformity", "inverted-segment",
                "parallel-anchors", "long-tail", "local-recompute", "concurrent-freeze"),
                "missing flags: " + string.Join(",", new[] {
                    "depth-duplicate","unconformity","inverted-segment","parallel-anchors",
                    "long-tail","local-recompute","concurrent-freeze" }.Except(report.Flags)));
        }
    }

    [Fact]
    public void LocalRecompute_IsScopedToAffectedSegmentOnly()
    {
        var (report, store) = Replay();
        using (store)
        {
            var step = Assert.Single(report.Steps, s => s.Op == "change-source");
            Assert.Equal(new[] { "S2" }, step.Affected);
        }
    }

    [Fact]
    public void ConflictingHumanDecisions_BothBranchesRetained()
    {
        var (report, store) = Replay();
        using (store)
        {
            var state = Projector.Project(store);
            var main = Assert.Single(state.Branches, b => b.Branch == "main");
            var alt = Assert.Single(state.Branches, b => b.Branch == "alt-1");
            // main：接受 A6r → S2 倒置无解（结构化状态保留）
            var s2Main = Assert.Single(main.Fit.Model!.Segments, s => s.Segment == "S2");
            Assert.Equal(FitStatus.Unsatisfiable, s2Main.Status);
            Assert.Contains(s2Main.Diagnostics, d => d.Code == "inverted-segment");
            // alt-1：否决 A6r → 旧 A6 复活，S2 恢复可解
            var s2Alt = Assert.Single(alt.Fit.Model!.Segments, s => s.Segment == "S2");
            Assert.Equal(FitStatus.Ok, s2Alt.Status);
            Assert.NotEmpty(s2Alt.Candidates);
            // 两个冲突决定都是机器可读事件
            var decisions = state.Events.Where(e => e.Kind == ModelEvent.DecisionKind).ToList();
            Assert.Equal(2, decisions.Count);
            Assert.Contains(decisions, d => d.PayloadJson.Contains("\"accept\"") && d.PayloadJson.Contains("A6r"));
            Assert.Contains(decisions, d => d.PayloadJson.Contains("\"reject\"") && d.PayloadJson.Contains("A6r"));
        }
    }

    [Fact]
    public void EveryFitEvent_RecordsDigestParamVersionAndParents()
    {
        var (report, store) = Replay();
        using (store)
        {
            var fits = store.Events().Where(e => e.Kind == ModelEvent.FitKind).ToList();
            Assert.NotEmpty(fits);
            foreach (var f in fits)
            {
                Assert.StartsWith("sha256:", f.InputDigest);
                Assert.StartsWith("params/", f.ParamVersion);
                Assert.NotEmpty(f.Parents);
            }
            // 参数修订前后使用不同参数版本
            Assert.Contains(fits, f => f.ParamVersion == "params/v1");
            Assert.Contains(fits, f => f.ParamVersion == "params/v2");
        }
    }

    [Fact]
    public void CaseModel_KeepsMultipleCandidates_AndMonotonicCurves()
    {
        var (report, store) = Replay();
        using (store)
        {
            var state = Projector.Project(store);
            var main = state.Branches.First(b => b.Branch == "main");
            var s1 = Assert.Single(main.Fit.Model!.Segments, s => s.Segment == "S1");
            Assert.True(s1.Candidates.Count >= 2, "equally-good candidates must be retained");
            foreach (var seg in main.Fit.Model.Segments.Where(s => s.Status == FitStatus.Ok))
                foreach (var c in seg.Candidates)
                    for (var i = 1; i < c.AgesKa.Length; i++)
                        Assert.True(c.AgesKa[i] >= c.AgesKa[i - 1] - 1e-9);
            // 所有领域量带单位与坐标基准；排除决定保留
            Assert.Equal("mbsf", state.Datum);
            Assert.Contains(state.Diagnostics, d => d.Code == "exclusion-kept" && d.TargetRef == "A10");
            // 冻结事件存在且并发冲突被记录
            Assert.Contains(state.Events, e => e.Kind == ModelEvent.FreezeKind);
            Assert.Contains(state.Events, e => e.Kind == ModelEvent.FreezeConflictKind);
        }
    }

    [Fact]
    public void HistoricalEvent_RestoresEarlierConclusions()
    {
        var (report, store) = Replay();
        using (store)
        {
            var full = Projector.Project(store);
            // 回到参数修订之前：应恢复 params/v1 与当时的储层偏移
            var revisionSeq = full.Events.First(e => e.Kind == ModelEvent.ParamRevisionKind).Seq;
            var before = Projector.Project(store, revisionSeq - 1);
            Assert.Equal("params/v1", before.Params.ParamVersion);
            Assert.Equal(0.40, before.Params.DefaultReservoirOffsetKa);
            var after = Projector.Project(store, revisionSeq);
            Assert.Equal("params/v2", after.Params.ParamVersion);
            Assert.Equal(0.34, after.Params.DefaultReservoirOffsetKa);
            // 回到源数据改动之前：S2 应当可解（A6 原值）
            var changeSeq = full.Events.First(e => e.Kind == ModelEvent.SourceChangeKind).Seq;
            var beforeChange = Projector.Project(store, changeSeq - 1);
            var s2 = Assert.Single(beforeChange.Branches.First(b => b.Branch == "main").Fit.Model!.Segments, s => s.Segment == "S2");
            Assert.Equal(FitStatus.Ok, s2.Status);
        }
    }
}
