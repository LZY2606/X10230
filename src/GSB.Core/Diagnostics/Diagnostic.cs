using System.Collections.Generic;
using System.Linq;

namespace GSB.Core.Diagnostics;

/// <summary>
/// 带位置的诊断。输入不完整/不可转换时返回诊断而不是用空值或默认值伪装成功。
/// Severity 区分错误（阻止该派生）与警告（保留材料、标注失效）。
/// </summary>
public sealed record Diagnostic(string Code, string Severity, string Path, string Message, string? Value = null)
{
    public bool IsError => Severity == "error";

    public static Diagnostic Error(string code, string path, string message, string? value = null)
        => new(code, "error", path, message, value);
    public static Diagnostic Warning(string code, string path, string message, string? value = null)
        => new(code, "warning", path, message, value);
}

public sealed record DiagnosticBag(List<Diagnostic> Items)
{
    public DiagnosticBag() : this(new List<Diagnostic>()) { }
    public bool HasErrors => Items.Any(d => d.IsError);
    public void Add(Diagnostic d) => Items.Add(d);
    public void AddRange(IEnumerable<Diagnostic> ds) => Items.AddRange(ds);
}
