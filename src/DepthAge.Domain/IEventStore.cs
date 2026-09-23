namespace DepthAge.Domain;

/// <summary>Raised when a bundle or payload cannot be parsed. Distinct from semantic failures.</summary>
public sealed class FormatCorruptException : Exception
{
    public string Position { get; }

    public FormatCorruptException(string position, string message)
        : base($"format corrupt at {position}: {message}") => Position = position;
}

/// <summary>Raised when an optimistic-concurrency check fails (e.g. concurrent freeze).</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public long Expected { get; }
    public long Actual { get; }

    public ConcurrencyConflictException(long expected, long actual)
        : base($"concurrent modification: expected branch seq {expected}, found {actual}")
    {
        Expected = expected;
        Actual = actual;
    }
}

/// <summary>
/// Append-only event store plus content-addressed derivation store. Implemented by SQLite;
/// tests and services call this interface directly.
/// </summary>
public interface IEventStore
{
    long Append(EventEnvelope envelope);

    /// <summary>Append only if the branch's last seq still equals expectedSeq; else throws.</summary>
    long AppendIfUnchanged(EventEnvelope envelope, long expectedSeq);

    IReadOnlyList<StoredEvent> ReadAll();
    IReadOnlyList<StoredEvent> ReadBranch(string branchId);
    long LastSeq { get; }

    void SaveDerivation(DerivationRecord record);
    DerivationRecord? GetDerivation(string derivationId);
    IReadOnlyList<DerivationRecord> AllDerivations();

    void SaveReport(string kind, string id, string json);
    string? GetReport(string kind, string id);
    IReadOnlyList<(string Kind, string Id)> ListReports(string kind);

    string ExportBundleJson();
    void ImportBundleJson(string json);
}
