namespace CoreChrono.Core.Domain;

/// <summary>带位置的诊断；输入不完整时返回它，绝不用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(string Code, string Path, string Message, string Severity)
{
    public static Diagnostic Error(string code, string path, string message) => new(code, path, message, "error");
    public static Diagnostic Warning(string code, string path, string message) => new(code, path, message, "warning");
    public override string ToString() => $"[{Severity}] {Code} @ {Path}: {Message}";
}
