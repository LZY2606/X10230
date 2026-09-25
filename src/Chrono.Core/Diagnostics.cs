namespace Chrono.Core;

public enum OutcomeKind
{
    /// <summary>格式损坏：词法/语法层面无法解析，必须带位置。</summary>
    FormatCorrupt,
    /// <summary>语义无解：可解析但领域规则冲突（如倒置且无边界声明）。</summary>
    SemanticImpossible,
    /// <summary>资源预算耗尽：枚举候选/采样超过预算，结果与前两者区分。</summary>
    BudgetExhausted,
}

public sealed record Diagnostic(OutcomeKind Kind, string Code, string Message, string? SourceRef = null, int? Line = null, int? Column = null)
{
    public string Location => Line is null ? SourceRef ?? "?" : $"{SourceRef ?? "?"}:{Line}{(Column is { } c ? ":" + c : "")}";
}

/// <summary>输入不完整/失败时的统一返回：绝不以 null 或默认值伪装成功。</summary>
public sealed class IngestResult<T>
{
    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; private init; } = Array.Empty<Diagnostic>();

    public static IngestResult<T> Success(T value) => new() { Ok = true, Value = value };
    public static IngestResult<T> Failure(IEnumerable<Diagnostic> diags) => new() { Ok = false, Diagnostics = diags.ToList() };
    public static IngestResult<T> Failure(params Diagnostic[] diags) => new() { Ok = false, Diagnostics = diags };
}

/// <summary>重放/推导预算。耗尽必须区别于格式错误与语义无解。</summary>
public sealed class ComputeBudget(int maxCandidates, int maxSamples, int maxSteps)
{
    public int MaxCandidates { get; } = maxCandidates;
    public int MaxSamples { get; } = maxSamples;
    public int MaxSteps { get; } = maxSteps;
    public int CandidatesUsed { get; set; }
    public int SamplesUsed { get; set; }
    public int StepsUsed { get; set; }
    public bool TryUseCandidate()
    {
        if (CandidatesUsed >= MaxCandidates) return false;
        CandidatesUsed++;
        return true;
    }
    public bool TryUseSamples(int n)
    {
        if (SamplesUsed + n > MaxSamples) return false;
        SamplesUsed += n;
        return true;
    }
    public bool TryStep()
    {
        if (StepsUsed >= MaxSteps) return false;
        StepsUsed++;
        return true;
    }
}
