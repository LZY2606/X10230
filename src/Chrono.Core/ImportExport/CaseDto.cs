using System.Text.Json.Nodes;

namespace Chrono.Core.ImportExport;

/// <summary>
/// Raw shape of an offline case pack. Values are intentionally strings: the original
/// representation (units, datums, conventions) must survive import untouched.
/// </summary>
public sealed class CasePack
{
    public string CaseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Scenario { get; set; } = "";
    public JsonNode? Root { get; set; }
}

public sealed record ImportOutcome(
    string Status,
    Model.ProjectMaterials? Materials,
    IReadOnlyList<Model.Diagnostic> Diagnostics,
    string? FormatError)
{
    public bool Ok => Status == "ok";
}
