namespace ChronoCore;

public enum DiagSeverity { Info, Warning, Error }

/// <summary>带位置的诊断；输入不完整时返回，而非以空值/默认值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    string? MaterialId = null,
    string? Field = null,
    string? Location = null,
    DiagSeverity Severity = DiagSeverity.Error)
{
    public string Display =>
        $"[{Code}] {Message}" +
        (MaterialId is null ? "" : $" @材料 {MaterialId}") +
        (Field is null ? "" : $".{Field}") +
        (Location is null ? "" : $" 位置:{Location}");
}

/// <summary>三种必须严格区分的结果类别（外加 Success）。</summary>
public enum OutcomeKind { Success, BudgetExhausted, FormatCorrupted, SemanticallyImpossible }

public sealed record DeriveOutcome(
    OutcomeKind Kind,
    IReadOnlyList<DerivedModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    string InputDigest,
    string ParameterVersion,
    IReadOnlyList<string> ParentDependencies,
    int BudgetRequested,
    int BudgetUsed)
{
    public bool Ok => Kind == OutcomeKind.Success;
    public static DeriveOutcome Fail(OutcomeKind kind, Diagnostic d, string digest, string pv,
        IReadOnlyList<string>? parents = null, int budgetRequested = 0, int budgetUsed = 0)
        => new(kind, Array.Empty<DerivedModel>(), new[] { d }, digest, pv,
           parents ?? Array.Empty<string>(), budgetRequested, budgetUsed);
}
