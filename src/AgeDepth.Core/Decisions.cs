namespace AgeDepth.Core;

/// <summary>
/// Mutable decisions live in a separate layer above immutable materials.
/// Every decision (including exclusions) is preserved; later decisions may
/// supersede earlier ones but never erase them.
/// </summary>
public sealed record Decision
{
    public string Id { get; init; } = "";
    /// <summary>exclude-anchor | include-anchor | revise-parameter | adopt-candidate | reject-candidate | note</summary>
    public string Kind { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Operator { get; init; } = "";
    public Dictionary<string, string> Params { get; init; } = new();
}
