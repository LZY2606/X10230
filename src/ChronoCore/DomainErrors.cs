namespace ChronoCore;

public sealed class DamagedPackageException : Exception
{
    public string Position { get; }

    public DamagedPackageException(string message, string position = "$") : base(message)
    {
        Position = position;
    }
}

public sealed class SemanticModelException : Exception
{
    public string ObjectId { get; }
    public string Position { get; }

    public SemanticModelException(string code, string message, string objectId = "", string position = "$")
        : base($"{code}: {message}")
    {
        ObjectId = objectId;
        Position = position;
    }
}

public sealed class ResourceBudgetExhaustedException : Exception
{
    public string Budget { get; }
    public int Used { get; }
    public int Limit { get; }

    public ResourceBudgetExhaustedException(string budget, int used, int limit)
        : base($"Budget {budget} exhausted: {used} > {limit}")
    {
        Budget = budget;
        Used = used;
        Limit = limit;
    }
}

public sealed class ConcurrentProjectException : Exception
{
    public string ExpectedParentEventId { get; }
    public string ActualParentEventId { get; }

    public ConcurrentProjectException(string expected, string actual)
        : base("Project state changed before the operation completed")
    {
        ExpectedParentEventId = expected;
        ActualParentEventId = actual;
    }
}
