using ChronoCore;
using ChronoStorage;
using ChronoWeb;
using Xunit;

namespace ChronoTests;

public sealed class StorageRoundTripTests : IDisposable
{
    private readonly string _dir;
    public StorageRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "chrono-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Db(string name) => Path.Combine(_dir, name + ".db");

    private (ChronoRepository repo, SeedingSummary summary) Seeded()
    {
        var repo = new ChronoRepository(Db("a"));
        var summary = new WorkflowSeederShim(repo).Seed();
        return (repo, summary);
    }

    [Fact]
    public void Export_DeleteDb_ReImport_PreservesIdentitiesOrderAndProvenance()
    {
        var (repo1, _) = Seeded();
        var zip = repo1.ExportBundle("test");

        var eventsBefore = repo1.ReadAllEvents().OrderBy(e => e.BranchId).ThenBy(e => e.Seq)
            .Select(e => (e.BranchId, e.Seq, e.Id, e.Type, e.Adopted, e.InputDigest, e.ParameterVersion))
            .ToList();
        var branchesBefore = repo1.ListBranches().Select(b =>
            (b.BranchId, b.ParentBranchId, b.FromSeq, b.BaselineDigest, b.ChangedSourceId, b.Purpose)).ToList();
        var derivedBefore = repo1.ListBranches().SelectMany(b =>
            repo1.ReadDerivedModels(b.BranchId).Select(m => (b.BranchId, m.Id, m.InputDigest, m.ParameterVersion, m.Rank)))
            .OrderBy(x => x).ToList();

        // 删除本地数据库 → 全新库导入
        var repo2 = new ChronoRepository(Db("b"));
        var stats = repo2.ImportBundle(zip, replaceExisting: true);
        Assert.True(stats.Events == eventsBefore.Count);
        Assert.Equal(branchesBefore.Count, stats.Branches);

        var eventsAfter = repo2.ReadAllEvents().OrderBy(e => e.BranchId).ThenBy(e => e.Seq)
            .Select(e => (e.BranchId, e.Seq, e.Id, e.Type, e.Adopted, e.InputDigest, e.ParameterVersion))
            .ToList();
        Assert.Equal(eventsBefore, eventsAfter);

        var branchesAfter = repo2.ListBranches().Select(b =>
            (b.BranchId, b.ParentBranchId, b.FromSeq, b.BaselineDigest, b.ChangedSourceId, b.Purpose)).ToList();
        Assert.Equal(branchesBefore, branchesAfter);

        var derivedAfter = repo2.ListBranches().SelectMany(b =>
            repo2.ReadDerivedModels(b.BranchId).Select(m => (b.BranchId, m.Id, m.InputDigest, m.ParameterVersion, m.Rank)))
            .OrderBy(x => x).ToList();
        Assert.Equal(derivedBefore, derivedAfter);

        // 父级依赖链保留
        var derivedEv = repo2.ReadAllEvents().First(e => e.Type == EventType.ModelsDerived);
        Assert.NotEmpty(derivedEv.InputDigest);
        Assert.NotEmpty(derivedEv.Parents);
    }

    [Fact]
    public void CorruptedZip_FailsAsFormatCorrupted_AndWritesNothing()
    {
        var (repo1, _) = Seeded();
        var beforeCount = repo1.ReadAllEvents().Count;

        var badZip = new byte[] { 80, 75, 3, 4, 1, 2, 3, 99 };
        var ex = Assert.Throws<FormatCorruptedImportException>(
            () => repo1.ImportBundle(badZip, replaceExisting: true));
        Assert.False(string.IsNullOrEmpty(ex.Message));
        Assert.Equal(beforeCount, repo1.ReadAllEvents().Count);

        // 合法 zip 但缺清单
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms,
                   System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var e = z.CreateEntry("events.jsonl");
            using var w = new StreamWriter(e.Open()); w.WriteLine("{}");
        }
        var ex2 = Assert.Throws<FormatCorruptedImportException>(
            () => repo1.ImportBundle(ms.ToArray(), replaceExisting: true));
        Assert.Contains("manifest", ex2.Message);
        Assert.Equal(beforeCount, repo1.ReadAllEvents().Count);
    }

    [Fact]
    public void HalfWrittenBatch_IsDetectedAndRecovered_OnNextOpen()
    {
        var db = Db("half");
        var repo = new ChronoRepository(db);
        repo.EnsureProject("p", "n");
        repo.InjectHalfWriteForRecovery("simulated-crash");

        var ex = Assert.Throws<HalfWriteRecoveredException>(() => new ChronoRepository(db));
        Assert.Contains("半写", ex.Message);
        // 再打开：恢复完成，正常可用
        var repo2 = new ChronoRepository(db);
        repo2.EnsureProject("p", "n");
        Assert.True(repo2.ProjectExists());
    }

    [Fact]
    public void FoldBackToOldEvent_RestoresPastConclusions_NotLatestOverride()
    {
        var (repo, _) = Seeded();
        var all = repo.ReadAllEvents();
        var mainEvents = all.Where(e => e.BranchId == "main").OrderBy(e => e.Seq).ToList();

        var firstDerive = mainEvents.First(e => e.Type == EventType.ModelsDerived);
        var past = EventFolder.FoldTo(all, "main", firstDerive.Seq);
        var head = EventFolder.FoldTo(all, "main", null);

        // 早期状态参数版本是 v1；折叠到之后的参数修订事件则是 v2/v3
        Assert.Equal("v1", past.CurrentParameterVersion);
        Assert.NotEqual("v1", head.CurrentParameterVersion);
        // 早期派生模型快照仍可读回，身份不变
        var snapshot = repo.ReadDerivedModels("main")
            .FirstOrDefault(m => m.InputDigest == firstDerive.InputDigest && m.ParameterVersion == "v1");
        Assert.NotNull(snapshot);
    }
}
