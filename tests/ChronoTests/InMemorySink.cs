using ChronoCore;

namespace ChronoTests;

public sealed class InMemoryEventSink : IEventSink
{
    private readonly List<ChronoEvent> _events = new();
    private readonly Dictionary<string, (string holder, string since)> _locks = new();
    public IReadOnlyList<ChronoEvent> Events => _events;
    private long _seq;

    public void EnsureProject(string projectId, string name) { }

    public ChronoEvent Append(ChronoEvent ev, IReadOnlyList<DerivedModel>? snapshots,
        string? freezeModelId, string? freezeHolderActor, string actor)
    {
        if (freezeModelId is not null && freezeHolderActor is not null &&
            _locks.TryGetValue(freezeModelId, out var l) && l.holder != freezeHolderActor)
            throw new ConcurrentFreezeConflict(freezeModelId, l.holder, l.since);
        if (freezeModelId is not null && freezeHolderActor is not null)
            _locks[freezeModelId] = (freezeHolderActor, ev.Id);
        var stored = ev with { Seq = Interlocked.Increment(ref _seq) };
        _events.Add(stored);
        return stored;
    }

    public void Fork(string newBranch, string parentBranch, long fromSeq, string projectId,
        string? baselineDigest, string? changedSourceId, string purpose) { }
}

public static class TestHarness
{
    public static (CaseBundle bundle, ProjectState state, ParameterSet p1, ChronoAppService app, InMemoryEventSink sink)
        Baseline(string parameterVersion = "v1")
    {
        var bundle = CaseBuilder.Build();
        var sink = new InMemoryEventSink();
        var app = new ChronoAppService(sink);
        app.SeedFromCase(bundle);
        var state = EventFolder.FoldTo(sink.Events, "main", null);
        var p = state.Parameters[parameterVersion];
        return (bundle, state, p, app, sink);
    }
}
