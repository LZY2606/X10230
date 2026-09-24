namespace AgeModel.Core;

public interface IEventStore
{
    void Append(EventEnvelope evt);
    IReadOnlyList<EventEnvelope> List(string projectId);
    long NextSequence(string projectId);
    void ReplaceProject(string projectId, IReadOnlyList<EventEnvelope> events);
    IReadOnlyList<RecoveredWrite> RecoverPendingWrites();
    void LeaveWriteIntent(EventEnvelope evt);
    void AppendWithoutCommitToSimulateCrash(EventEnvelope evt);
}

public sealed record RecoveredWrite(string EventId, string ProjectId, long Sequence, string Reason);
