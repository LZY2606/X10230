namespace ChronoCore.Import;

public enum ImportStatus { Success = 0, MalformedFormat = 1, SemanticallyUnsolvable = 2, BudgetExhausted = 3 }

public enum Severity { Error = 0, Warning = 1, Info = 2 }

public readonly record struct SourcePosition(string File, string? JsonPointer, int? ElementIndex)
{
    public override string ToString() =>
        File + (ElementIndex is int i ? $"[{i}]" : "") + (JsonPointer is { } p ? "/" + p : "");
}

public sealed record ImportDiagnostic(Severity Severity, SourcePosition Position, string Code, string Message)
{
    public string Location => Position.ToString();
}
