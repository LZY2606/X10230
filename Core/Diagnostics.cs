namespace DepthAge.Core;

/// <summary>结构化诊断：带稳定对象身份与位置（材料/锚点路径或导入 JSON 路径），绝不靠空值伪装成功。</summary>
public sealed record Diagnostic(string Code, string Severity, string Message, string? TargetRef, string? Position)
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>资源预算耗尽 / 格式损坏 / 语义无解：三种必须可区分的结果。</summary>
public enum FitStatus { Ok, BudgetExhausted, Unsatisfiable }
