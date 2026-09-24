using Chrono.Core.Domain;

namespace Chrono.Storage;

public interface IEventStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StoredEvent>> LoadAllAsync(CancellationToken ct = default);
    Task<AppendResult> AppendAsync(StoredEvent e, string? expectedLastEventId, CancellationToken ct = default);

    /// <summary>When non-null, the next write simulates a crash mid-commit: the
    /// payload is truncated and the row is left without a commit marker,
    /// exercising half-write recovery.</summary>
    void ArmHalfWriteCrash(string marker);
}

public sealed record AppendResult(bool Ok, string? ConflictActualLastId, StoredEvent? Stored);

public sealed record EventEnvelope(string SchemaVersion, IReadOnlyList<StoredEvent> Events);
