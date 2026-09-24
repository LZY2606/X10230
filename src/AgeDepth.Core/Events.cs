namespace AgeDepth.Core;

/// <summary>机器可读取的判断事件。所有状态变化都是事件，可重放。</summary>
public sealed record EventRecord(
    long Seq, string Id, string Kind, string At,
    string ParamVersion, string InputHash, string[] Parents, string Payload);

public static class EventKinds
{
    public const string MaterialsImported = "materials.imported";
    public const string AnchorAdded = "anchor.added";
    public const string AnchorStatus = "anchor.status";
    public const string ParamRevised = "param.revised";
    public const string ModelComputed = "model.computed";
    public const string ModelFrozen = "model.frozen";
    public const string ModelFreezeRejected = "model.freeze.rejected";
    public const string DecisionRecorded = "decision.recorded";
    public const string CaseReplayed = "case.replayed";
}

public sealed class ConcurrencyConflictException : Exception
{
    public long Expected { get; }
    public long Actual { get; }
    public ConcurrencyConflictException(long expected, long actual)
        : base($"并发冲突: 期望序列 {expected}, 实际 {actual}")
    { Expected = expected; Actual = actual; }
}

/// <summary>格式损坏（区别于预算耗尽与语义无解）。</summary>
public sealed class FormatCorruptException : Exception
{
    public string Position { get; }
    public FormatCorruptException(string message, string position) : base(message) { Position = position; }
}
