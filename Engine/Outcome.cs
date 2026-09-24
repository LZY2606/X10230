namespace CoreChrono.Engine;

public enum OutcomeKind
{
    Ok, FormatCorrupt, SemanticNoSolution, ResourceBudgetExhausted,
    IncompleteInput, ConcurrentFreezeConflict, NotFound
}

public sealed class EngineResult<T>
{
    public OutcomeKind Kind { get; private set; }
    public T? Value { get; private set; }
    public IReadOnlyList<Domain.Diagnosis> Diagnostics { get; private set; } = Array.Empty<Domain.Diagnosis>();
    public bool Ok => Kind == OutcomeKind.Ok;

    public static EngineResult<T> Success(T value, IEnumerable<Domain.Diagnosis>? diags = null)
        => new() { Kind = OutcomeKind.Ok, Value = value, Diagnostics = (diags ?? Array.Empty<Domain.Diagnosis>()).ToList() };
    public static EngineResult<T> Fail(OutcomeKind kind, IEnumerable<Domain.Diagnosis> diags)
        => new() { Kind = kind, Diagnostics = diags.ToList() };

    public EngineResult<TOut> Map<TOut>(Func<T, TOut> f)
        => Ok ? EngineResult<TOut>.Success(f(Value!), Diagnostics) : EngineResult<TOut>.Fail(Kind, Diagnostics);
}
