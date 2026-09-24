using System.Text.Json;

namespace Chrono.Core;

public sealed record BranchInfo(string BranchId, string ParentBranchId, int ForkSeq, string Reason, int Seq);

public sealed record BundleInfo(string ProjectId, string ProjectName, string CaseId, string ExportedAtUtc, int EventCount);

/// <summary>Everything persistence must provide; SQLite and in-memory fakes both implement it.</summary>
public interface IProjectStore : IDisposable
{
    void Initialize();
    string CreateProject(string projectId, string name, string caseId);
    bool ProjectExists(string projectId);
    BundleInfo? LoadBundleInfo(string projectId);

    /// <summary>Read branch event stream (branch events only).</summary>
    IReadOnlyList<ChronoEvent> ReadStream(string projectId, string branchId);

    /// <summary>Optimistic append. Returns the new event or null on version conflict (concurrent write).</summary>
    ChronoEvent? TryAppend(string projectId, string branchId, int expectedVersion, EventPayload payload,
        string author, string inputDigest, string? parentEventId, string createdAtUtc);

    IReadOnlyList<BranchInfo> ListBranches(string projectId);
    string CreateBranch(string projectId, string newBranchId, string fromBranchId, int forkSeq, string reason);

    void ExportBundle(string projectId, Stream destination);

    /// <summary>
    /// Import a bundle. <paramref name="simulateCrashAfterEvents"/> aborts mid-transaction so tests
    /// can prove half-written imports are rolled back and the prior database is intact.
    /// </summary>
    (string projectId, bool replaced) ImportBundle(Stream source, bool overwrite = false,
        int? simulateCrashAfterEvents = null);

    void DeleteProject(string projectId);
    IReadOnlyList<string> ListProjects();
}

public class CommandRejectedException : Exception
{
    public string Code { get; }
    public CommandRejectedException(string code, string message) : base(message) => Code = code;
}

/// <summary>Result of a command: the appended event plus the projected state after it.</summary>
public sealed record CommandResult(ChronoEvent Event, BranchState State, bool Conflicted);
