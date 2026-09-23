namespace ChronoStorage;

public sealed class ConcurrentFreezeException(string modelId, string holderActor)
    : Exception($"模型 {modelId} 已被 {holderActor} 冻结，并发冻结必须形成冲突事件而不是覆盖")
{
    public string ModelId { get; } = modelId;
    public string HolderActor { get; } = holderActor;
}

public sealed class FormatCorruptedImportException(string message, string? location = null)
    : Exception(message)
{
    public string? Location { get; } = location;
}
