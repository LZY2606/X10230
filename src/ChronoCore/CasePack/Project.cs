using ChronoCore.Domain;

namespace ChronoCore.CasePack;

/// <summary>案例包项目：不可变材料 + 可修改决定 + 叙事步骤（一个完整故事，不是散落按钮）。</summary>
public sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string NarrativeTitle { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<Material> Materials { get; set; } = new();
    public List<Decision> Decisions { get; set; } = new();
    public List<ParameterSet> ParameterSets { get; set; } = new();
    public List<Branch> Branches { get; set; } = new();
    public List<CaseStep> Script { get; set; } = new();

    public Project WithScript()
    {
        Script.AddRange(CaseScript.Build());
        return this;
    }
}

public sealed record CaseStep
{
    public string StepId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Expectation { get; init; } = "";
    public string[] BoundaryTags { get; init; } = Array.Empty<string>();
}
