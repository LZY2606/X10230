using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;
using Xunit;

namespace AgeDepthStudio.Tests.Store;

public class EventStoreTests
{
    [Fact]
    public void Export_delete_import_preserves_identity_order_and_provenance()
    {
        var path1 = TestSupport.TempDb();
        var store = new EventStore(path1);
        var svc = new StudioService(store);
        TestSupport.MiniProject(svc);
        svc.AddAnchor("T", new AgeAnchor("A1", "S1", "1m", "L1", "f", TestSupport.Samples(10, .3, seed: 1)));
        svc.RecordDecision("T", new Decision("D1", "A1", DecisionAction.ExcludeAnchor, "r", "x", "2026-09-21T00:00:00Z"));
        svc.Recompute("T", "M1");
        var original = store.Events("T");
        var archive = store.ExportArchive("T");
        store.Dispose();
        File.Delete(path1);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path1)!, Path.GetFileName(path1) + "*"))
            File.Delete(f);
        Assert.False(File.Exists(path1));

        var store2 = new EventStore(path1);
        store2.ImportArchive(archive);
        var restored = store2.Events("T");
        Assert.Equal(original.Select(e => e.Seq), restored.Select(e => e.Seq));
        Assert.Equal(original.Select(e => e.Id), restored.Select(e => e.Id));
        Assert.Equal(original.Select(e => e.ParentHash), restored.Select(e => e.ParentHash));
        Assert.Equal(original.Select(e => e.Hash), restored.Select(e => e.Hash));
        Assert.True(store2.VerifyChain("T"));
        var state = Projector.Project(restored);
        Assert.Equal("A1", state.Anchors[0].AnchorId);
        Assert.Equal("M1", state.Models[0].ModelId);
        store2.Dispose();
    }

    [Fact]
    public void Tampered_archive_is_rejected()
    {
        var (svc, store) = TestSupport.New();
        TestSupport.MiniProject(svc);
        svc.Recompute("T", "M1");
        var archive = store.ExportArchive("T");
        var tampered = archive.Replace("\"actor\":\"engine\"", "\"actor\":\"attacker\"");
        if (tampered == archive) tampered = archive.Replace("M1", "M999");
        var path = TestSupport.TempDb();
        using var target = new EventStore(path);
        var ex = Assert.ThrowsAny<System.IO.InvalidDataException>(() => target.ImportArchive(tampered));
        Assert.Contains("hash chain", ex.Message);
    }

    [Fact]
    public void Half_written_import_leaves_existing_database_intact()
    {
        var path = TestSupport.TempDb();
        var store = new EventStore(path);
        var svc = new StudioService(store);
        TestSupport.MiniProject(svc);
        svc.Recompute("T", "M1");
        var beforeCount = store.Events("T").Count;

        // 模拟写入中途崩溃：留下 .importing 残骸，主库必须完好并可继续工作。
        File.WriteAllText(path + ".importing", "garbage-partial-write");
        var reopened = new EventStore(path); // 构造函数执行半写恢复
        Assert.Equal(beforeCount, reopened.Events("T").Count);
        reopened.Append("probe.after_crash", "T", "test", "{}");
        Assert.Equal(beforeCount + 1, reopened.Events("T").Count);
        Assert.False(File.Exists(path + ".importing"));
        reopened.Dispose();
        store.Dispose();
    }

    [Fact]
    public void Corrupt_json_import_raises_parse_error_distinct_from_semantic()
    {
        var path = TestSupport.TempDb();
        using var store = new EventStore(path);
        Assert.Throws<System.Text.Json.JsonException>(() => store.ImportArchive("{not json"));
    }

    [Fact]
    public void Hash_chain_anchors_parent_links()
    {
        var (svc, store) = TestSupport.New();
        TestSupport.MiniProject(svc);
        var events = store.Events("T");
        Assert.Equal("genesis", events[0].ParentHash);
        for (var i = 1; i < events.Count; i++) Assert.Equal(events[i - 1].Hash, events[i].ParentHash);
        Assert.True(store.VerifyChain("T"));
    }

    [Fact]
    public void Stable_ids_survive_projection()
    {
        var (svc, store) = TestSupport.New();
        TestSupport.MiniProject(svc);
        svc.AddAnchor("T", new AgeAnchor("A-STABLE", "S1", "2.50m", "L", "f", TestSupport.Samples(12, .3, seed: 5)));
        svc.Recompute("T", "M1");
        var state = Projector.Project(store.Events("T"));
        var cand = state.Models[0].Segments.Single(r => r.SegmentId == "S1").Candidates[0];
        Assert.Contains("A-STABLE", cand.CandidateId);
    }
}
