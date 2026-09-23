namespace ChronoCore;

public sealed class ConcurrentFreezeConflict(string modelId, string holderActor, string heldSinceEventId)
    : Exception($"并发冻结冲突：{modelId} 已由 {holderActor} 持有")
{
    public string ModelId { get; } = modelId;
    public string HolderActor { get; } = holderActor;
    public string HeldSinceEventId { get; } = heldSinceEventId;
}

/// <summary>事件存储抽象：业务层不直接依赖 SQLite，存储层可被测试替身替换。</summary>
public interface IEventSink
{
    void EnsureProject(string projectId, string name);
    ChronoEvent Append(ChronoEvent ev, IReadOnlyList<DerivedModel>? snapshots,
        string? freezeModelId, string? freezeHolderActor, string actor);
    void Fork(string newBranch, string parentBranch, long fromSeq, string projectId,
        string? baselineDigest, string? changedSourceId, string purpose);
}
