using AgeDepth;
using Xunit;

namespace AgeDepth.Tests;

public class Fixture : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "agedepth-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public Fixture() => Directory.CreateDirectory(Dir);
    public Store NewStore(string name = "t.db") => new(Path.Combine(Dir, name));
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

public class EngineTests : IClassFixture<Fixture>
{
    private readonly Fixture _f;
    public EngineTests(Fixture f) => _f = f;

    private static AgeAnchor Anchor(string id, string seg, double depth, params double[] ages) =>
        new(id, seg, Quantity.Of(depth, "mbsf", "mbsf"),
            new AgeDistribution("samples", ages, "ka BP"), null, "test");

    private static List<CoreSegment> Segments() => new()
    {
        new CoreSegment("seg-A", "KR", Quantity.Of(0, "mbsf", "mbsf"), Quantity.Of(4.2, "mbsf", "mbsf"), "sediment", "clay"),
        new CoreSegment("unc-1", "KR", Quantity.Of(4.2, "mbsf", "mbsf"), Quantity.Of(4.2, "mbsf", "mbsf"), "unconformity", "erosion"),
        new CoreSegment("seg-B", "KR", Quantity.Of(4.2, "mbsf", "mbsf"), Quantity.Of(8.0, "mbsf", "mbsf"), "sediment", "silt"),
    };

    [Fact]
    public void DepthDuplicate_ProducesDiagnostic_AndTiedCandidates()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1, 2.2),
            Anchor("a2", "seg-A", 2.0, 4.0, 4.1, 4.2),
            Anchor("a2b", "seg-A", 2.0, 4.5, 4.6, 4.7),
        };
        var r = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        Assert.Contains(r.Segments.SelectMany(s => s.Diagnostics), d => d.Code == "DEPTH_DUPLICATE" && d.Depth == 2.0);
        var accepted = r.Segments.Single(s => s.SegmentId == "seg-A").Candidates.Where(c => c.Status == "accepted").ToList();
        Assert.Equal(2, accepted.Count); // both tie choices kept, never merged
    }

    [Fact]
    public void Unconformity_IsExplicitBarrier()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1),
            Anchor("a2", "seg-A", 3.0, 4.0, 4.1),
            Anchor("b1", "seg-B", 5.0, 9.0, 9.1),
            Anchor("b2", "seg-B", 7.0, 11.0, 11.1),
        };
        var r = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        Assert.All(r.AllCandidates.Where(c => c.Status == "accepted"), c =>
            Assert.True(c.DepthKnots.All(d => d < 4.2) || c.DepthKnots.All(d => d >= 4.2)));
        Assert.DoesNotContain(r.AllCandidates, c => c.SegmentId == "unc-1");
    }

    [Fact]
    public void InvertedSection_KeepsRejected_AndExclusionCandidates()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1),
            Anchor("a2", "seg-A", 2.0, 4.0, 4.1),
            Anchor("a3", "seg-A", 3.0, 0.5, 0.6), // inverted: younger deeper
        };
        var r = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        var seg = r.Segments.Single(s => s.SegmentId == "seg-A");
        Assert.Contains(seg.Diagnostics, d => d.Code == "INVERTED_SECTION");
        Assert.Contains(seg.Candidates, c => c.Status == "rejected"); // evidence retained
        Assert.Contains(seg.Candidates, c => c.Status == "accepted" && c.ExcludedAnchorIds.Contains("a3"));
    }

    [Fact]
    public void LongTail_BandIsAsymmetric_DistributionNotMean()
    {
        var ages = new[] { 7.9, 8.0, 8.0, 8.1, 8.1, 8.2, 9.5, 11.0, 14.5 };
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1),
            Anchor("a2", "seg-A", 3.0, ages),
        };
        var r = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        var c = r.AllCandidates.Single(x => x.UsedAnchorIds.Contains("a2"));
        var i = Array.IndexOf(c.UsedAnchorIds, "a2");
        var med = c.AgeMedian[i];
        Assert.True(c.AgeP95[i] - med > med - c.AgeP05[i]); // long tail preserved
        Assert.NotEqual(ages.Average(), med); // never collapsed to mean
    }

    [Fact]
    public void LocalRecompute_OnlyAffectedSegment()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1),
            Anchor("b1", "seg-B", 5.0, 9.0, 9.1),
        };
        var p = ModelEngine.DefaultParams;
        var before = ModelEngine.Compute(Segments(), anchors, p);
        var hashes = before.Segments.ToDictionary(s => s.SegmentId, s => s.InputHash);
        var changed = anchors.Select(a => a.Id == "b1" ? a with { Depth = Quantity.Of(5.5, "mbsf", "mbsf") } : a).ToList();
        var after = ModelEngine.Compute(Segments(), changed, p, hashes);
        Assert.True(after.Segments.Single(s => s.SegmentId == "seg-B").Recomputed);
        Assert.False(after.Segments.Single(s => s.SegmentId == "seg-A").Recomputed);
    }

    [Fact]
    public void BudgetExhausted_IsDistinctResult()
    {
        var anchors = new List<AgeAnchor>();
        for (var i = 0; i < 4; i++)
        {
            anchors.Add(Anchor($"x{i}a", "seg-A", i + 1.0, i + 2.0, i + 2.1));
            anchors.Add(Anchor($"x{i}b", "seg-A", i + 1.0, i + 2.05, i + 2.15));
        }
        var p = ModelEngine.DefaultParams with { CandidateBudget = 4 };
        var r = ModelEngine.Compute(Segments(), anchors, p);
        Assert.Equal(ResultKind.BudgetExhausted, r.Kind);
    }

    [Fact]
    public void SemanticUnsolvable_IsDistinctResult()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 3.0, 3.1),
            Anchor("a2", "seg-A", 2.0, 2.0, 2.1),
            Anchor("a3", "seg-A", 3.0, 1.0, 1.1),
        };
        var r = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        Assert.Equal(ResultKind.SemanticUnsolvable, r.Kind);
        Assert.Contains(r.Segments.SelectMany(s => s.Diagnostics), d => d.Code == "SEMANTIC_UNSOLVABLE");
    }

    [Fact]
    public void UnconvertibleDepth_KeptInPlaceWithDiagnostic()
    {
        var bad = new AgeAnchor("bad-1", "seg-A",
            Quantity.Unconvertible("约2.0?", "mbsf", "mbsf", "cannot convert"),
            new AgeDistribution("samples", new[] { 3.0, 3.1 }, "ka BP"), null, "handwritten");
        var r = ModelEngine.Compute(Segments(), new List<AgeAnchor> { bad }, ModelEngine.DefaultParams);
        var d = Assert.Single(r.Segments.SelectMany(s => s.Diagnostics));
        Assert.Equal("UNCONVERTIBLE_DEPTH", d.Code);
        Assert.Equal("bad-1", d.MaterialId);
    }

    [Fact]
    public void EquivalentInput_StableOrder()
    {
        var anchors = new List<AgeAnchor>
        {
            Anchor("a1", "seg-A", 1.0, 2.0, 2.1),
            Anchor("a2", "seg-A", 2.0, 4.0, 4.1),
            Anchor("a2b", "seg-A", 2.0, 4.5, 4.6),
        };
        var r1 = ModelEngine.Compute(Segments(), anchors, ModelEngine.DefaultParams);
        var r2 = ModelEngine.Compute(Segments(), anchors.OrderByDescending(a => a.Id).ToList(), ModelEngine.DefaultParams);
        Assert.Equal(r1.AllCandidates.Select(c => c.Id), r2.AllCandidates.Select(c => c.Id));
    }
}

public class StoreTests : IClassFixture<Fixture>
{
    private readonly Fixture _f;
    public StoreTests(Fixture f) => _f = f;

    private Store SeededStore(string name)
    {
        var s = _f.NewStore(name);
        CasePack.Seed(s, PackPath());
        return s;
    }

    public static string PackPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "casepack", "casepack.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("casepack.json");
    }

    [Fact]
    public void ConcurrentFreeze_OneWins_OneConflicts_BothRecorded()
    {
        using var s = SeededStore("freeze.db");
        var head = s.HeadSeq();
        var first = s.Freeze(head, "{\"m\":1}");
        var second = s.Freeze(head, "{\"m\":2}");
        Assert.Equal(ResultKind.Ok, first.Kind);
        Assert.Equal(ResultKind.Conflict, second.Kind);
        Assert.Contains(s.Events(), e => e.Kind == "freeze" && e.Status == "frozen");
        Assert.Contains(s.Events(), e => e.Kind == "freeze" && e.Status == "conflict");
    }

    [Fact]
    public void ExportImport_Roundtrip_PreservesIdentityOrderProvenance()
    {
        string export;
        using (var s = SeededStore("src.db")) export = s.Export();
        using var fresh = _f.NewStore("dst.db");
        var (kind, _) = fresh.Import(export);
        Assert.Equal(ResultKind.Ok, kind);
        Assert.Equal(export, fresh.Export()); // identity, order, provenance intact
    }

    [Fact]
    public void HalfWrittenImport_IsFormatCorrupt_AndLeavesDbUntouched()
    {
        using var s = SeededStore("half.db");
        var export = s.Export();
        var before = s.Events().Count;
        var truncated = export[..(export.Length / 2)];
        var (kind1, _) = s.Import(truncated);
        Assert.Equal(ResultKind.FormatCorrupt, kind1);
        Assert.Equal(before, s.Events().Count); // rolled back

        var tampered = export.Replace("\"ok\"", "\"ok-tampered\"");
        var (kind2, _) = s.Import(tampered);
        Assert.Equal(ResultKind.FormatCorrupt, kind2);
        Assert.Equal(before, s.Events().Count);
    }

    [Fact]
    public void Replay_RestoresHistoricalDependencyVersions()
    {
        using var s = SeededStore("replay.db");
        var events = s.Events();
        var paramRev = events.First(e => e.Kind == "parameter-revision");
        var before = CasePack.Replay(s, paramRev.Seq - 1);
        var after = CasePack.Replay(s, null);
        Assert.Equal("params/v1", before.Params.Version);
        Assert.Equal("params/v2", after.Params.Version);
        // historical view must not contain the later source change
        Assert.DoesNotContain(before.Anchors, a => a.Id == "anc-05-v2");
        Assert.Contains(after.Anchors, a => a.Id == "anc-05-v2");
    }

    [Fact]
    public void CasePack_Archive_AllScenariosConfirmed()
    {
        using var s = SeededStore("archive.db");
        var entries = ArchiveBuilder.Build(s);
        Assert.Equal(8, entries.Count); // baseline + 7 perturbations
        Assert.All(entries, e => Assert.Equal("confirmed", e.Outcome));
        // judgments are machine-readable events
        var judgments = s.Events().Where(e => e.Kind == "judgment").ToList();
        Assert.Equal(7, judgments.Count);
        Assert.Contains(judgments, j => j.Payload.Contains("\"R1\""));
        Assert.Contains(judgments, j => j.Payload.Contains("\"R2\""));
        Assert.Contains(judgments, j => j.Payload.Contains("\"R3\""));
    }

    [Fact]
    public void CasePack_PreservesRawRepresentations_AndDiagnostics()
    {
        using var s = SeededStore("raw.db");
        var state = CasePack.Replay(s, null);
        var bad = state.Anchors.Single(a => a.Id == "anc-08-bad");
        Assert.False(bad.Depth.Convertible);
        Assert.Equal("约5.0?", bad.Depth.Raw); // raw kept, not dropped for plotting
        Assert.Contains(state.Computation.Segments.SelectMany(x => x.Diagnostics),
            d => d.Code == "UNCONVERTIBLE_DEPTH" && d.MaterialId == "anc-08-bad");
        // reservoir offset present and applied in derived layer only
        var anc04 = state.Anchors.Single(a => a.Id == "anc-04");
        Assert.Equal(0.38, anc04.ReservoirOffset!.Value);
    }

    [Fact]
    public void ConflictingDecisions_AreBothRetained()
    {
        using var s = SeededStore("decisions.db");
        var state = CasePack.Replay(s, null);
        Assert.Equal(2, state.Decisions.Count);
        Assert.Contains(state.Decisions, d => d.Payload.Contains("accept"));
        Assert.Contains(state.Decisions, d => d.Payload.Contains("reject"));
    }
}
