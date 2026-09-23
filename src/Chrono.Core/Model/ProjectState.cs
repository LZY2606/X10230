using System.Collections.Generic;
using Chrono.Core.Materials;

namespace Chrono.Core.Model;

/// <summary>Material layer reconstructed by replaying events. Decisions never mutate materials.</summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string CoreId { get; set; } = "";
    public Dictionary<string, Material> Materials { get; } = new();
    public List<ExclusionDecisionMaterial> Exclusions { get; } = new();
    /// <summary>material id -> chosen correlation option id, per conflict group decision.</summary>
    public Dictionary<string, string> ChosenCorrelations { get; } = new();
    public RuleSet Rules { get; set; } = RuleSet.Default;
    public string? FrozenDerivationId { get; set; }
    public string? FrozenCandidateId { get; set; }
    public HashSet<string> RejectedCandidates { get; } = new();
}
