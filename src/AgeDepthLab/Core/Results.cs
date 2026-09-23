namespace AgeDepthLab.Core;

/// <summary>
/// 运行结果类别。资源预算耗尽、格式损坏、语义无解必须是三种不同结果，
/// 不得互相伪装，也不得用空值/默认值伪装成功。
/// </summary>
public enum ResultKind
{
    Ok,
    BudgetExhausted,   // 资源预算耗尽（如抽取次数超过上限）
    FormatCorrupt,     // 格式损坏（导入文件截断、校验失败、JSON 非法）
    SemanticUnsolvable // 语义无解（约束下不存在合法模型，如连续段内无法单调）
}

public sealed record RunDiagnostic(
    string Code,        // 机器可读代码，如 DEPTH_DUPLICATE / SEGMENT_INVERSION / UNIT_UNCONVERTIBLE
    string Severity,    // info | warning | error
    string Message,
    string? SubjectId,  // 受影响对象的稳定身份
    double? Position,   // 位置（深度，按声明基准）
    string? Datum);     // 位置所用坐标基准

public sealed class RunResult<T>
{
    public ResultKind Kind { get; }
    public T? Value { get; }
    public List<RunDiagnostic> Diagnostics { get; } = new();

    private RunResult(ResultKind kind, T? value) { Kind = kind; Value = value; }

    public static RunResult<T> Success(T value, IEnumerable<RunDiagnostic>? diags = null)
    {
        var r = new RunResult<T>(ResultKind.Ok, value);
        if (diags != null) r.Diagnostics.AddRange(diags);
        return r;
    }

    public static RunResult<T> Failure(ResultKind kind, params RunDiagnostic[] diags)
    {
        if (kind == ResultKind.Ok) throw new ArgumentException("failure kind must not be Ok");
        var r = new RunResult<T>(kind, default);
        r.Diagnostics.AddRange(diags);
        return r;
    }

    public bool IsOk => Kind == ResultKind.Ok;
}
