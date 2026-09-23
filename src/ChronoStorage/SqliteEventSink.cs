using ChronoCore;

namespace ChronoStorage;

public sealed class SqliteEventSink(ChronoRepository repo) : IEventSink
{
    public void EnsureProject(string projectId, string name) => repo.EnsureProject(projectId, name);

    public ChronoEvent Append(ChronoEvent ev, IReadOnlyList<DerivedModel>? snapshots,
        string? freezeModelId, string? freezeHolderActor, string actor)
    {
        try
        {
            return repo.AppendEvent(ev, snapshots, actor, freezeModelId, freezeHolderActor);
        }
        catch (ConcurrentFreezeException ex)
        {
            var held = repo.ReadAllEvents().Where(e => e.DerivedModelId == ex.ModelId)
                .Select(e => e.Id).FirstOrDefault() ?? ev.Id;
            throw new ConcurrentFreezeConflict(ex.ModelId, ex.HolderActor, held);
        }
    }

    public void Fork(string newBranch, string parentBranch, long fromSeq, string projectId,
        string? baselineDigest, string? changedSourceId, string purpose)
        => repo.ForkBranch(newBranch, parentBranch, fromSeq, projectId, baselineDigest, changedSourceId, purpose);
}
