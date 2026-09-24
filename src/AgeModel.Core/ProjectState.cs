using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel.Core;

/// <summary>事件流在某个时点的投影。回到任意旧事件时，重建当时的依赖版本与可见结论。</summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string CoreId { get; set; } = "";
    public CoreMaterial? Material { get; set; }
    public string ParameterVersion { get; set; } = AgeModelEngine.CurrentParameterVersion;
    public EngineParameters Parameters { get; set; } = new(AgeModelEngine.CurrentParameterVersion);
    public List<DerivationResult> Derivations { get; } = new();
    public List<JsonElement> Decisions { get; } = new();
    public Dictionary<string, CoreMaterial> MaterialVersions { get; } = new();
    public HashSet<string> FrozenBranches { get; } = new();
    public string? FrozenModelId { get; set; }
    public long? FrozenAtSeq { get; set; }
    public StoredEvent? LastEvent { get; set; }
    public long HeadSeq { get; set; }
    public List<StoredEvent> EventTimeline { get; } = new();

    public DerivationResult? LatestDerivation =>
        Derivations.OrderByDescending(d => d.RecomputedAt).FirstOrDefault();
}

/// <summary>分支时间线：分叉点 seq 之后的事件仅属于该分支。</summary>
public sealed record BranchInfo(string Name, string FromBranch, long AtSeq);
