namespace ChronoAge.Core;

public sealed record OperationResult<T>(
    OutcomeStatus Status,
    T? Value,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? CorrelationId = null)
{
    public bool Succeeded => Status is OutcomeStatus.Success or OutcomeStatus.CompletedWithDiagnostics;

    public static OperationResult<T> Ok(T value, IReadOnlyList<Diagnostic>? diagnostics = null, string? correlationId = null) =>
        new(diagnostics is { Count: > 0 } ? OutcomeStatus.CompletedWithDiagnostics : OutcomeStatus.Success,
            value, diagnostics ?? Array.Empty<Diagnostic>(), correlationId);

    public static OperationResult<T> Fail(OutcomeStatus status, IReadOnlyList<Diagnostic> diagnostics, string? correlationId = null) =>
        new(status, default, diagnostics, correlationId);
}
