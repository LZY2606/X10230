namespace AgeDepth.Core;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// A positional diagnostic. <see cref="Path"/> uses a JSON-pointer-like material
/// coordinate so an unconvertible or incomplete value can be found in place.
/// </summary>
public sealed record Diag(string Code, Severity Severity, string Path, string Message)
{
    public override string ToString() => $"[{Severity}] {Code} @ {Path}: {Message}";
}

/// <summary>
/// The three mandated distinct terminal outcomes plus ordinary success and the
/// incomplete-input case (which is never a silent success).
/// </summary>
public enum EngineStatus
{
    Ok = 0,
    Incomplete = 1,
    FormatCorrupt = 2,
    SemanticNoSolution = 3,
    BudgetExhausted = 4
}
