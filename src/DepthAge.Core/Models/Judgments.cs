namespace DepthAge.Core.Models;

/// <summary>带位置的诊断；输入不完整时返回诊断而不是空值/默认值。</summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required string Severity { get; init; } // Error | Warning | Info
    public required string Message { get; init; }
    public string? MaterialId { get; init; }
    public string? FieldPath { get; init; }         // 例如 anchors[3].age.reportedAge
    public double? DepthM { get; init; }
    public string? SegmentId { get; init; }
}

/// <summary>
/// 机器可读判定事件。规则的每一条结论（接受/拒绝/边界/候选保留原因）
/// 都落成结构化记录，而不是只写自然语言日志。
/// </summary>
public sealed record Judgment
{
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Status { get; init; }     // Accepted | Rejected | Boundary | Retained | Blocked | Warning
    public required string Statement { get; init; }
    public List<string> AffectedIds { get; init; } = new();
    public string? SegmentId { get; init; }
    public double? DepthM { get; init; }
    public string? RuleVersion { get; init; }
    public string? BasisId { get; init; }            // 依据（材料/规则/决定）id
    public Dictionary<string, string> Attributes { get; init; } = new();
    public required DateTimeOffset RecordedAt { get; init; }
}
