using System.Text.Json.Serialization;

namespace Chrono.Core;

/// <summary>
/// The offline domain case pack: "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// A manifest plus original source documents — never a generic spreadsheet.
/// </summary>
public sealed class CaseManifest
{
    public string CaseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public CaseSources Sources { get; set; } = new();
    public List<CaseStep> Narrative { get; set; } = new();
    public List<CounterExampleSpec> CounterExamples { get; set; } = new();
}

public sealed class CaseSources
{
    public string Dir { get; set; } = "sources/baseline";
    public string CoreLog { get; set; } = "corelog.json";
    public string AgeTable { get; set; } = "ages.json";
    public string Tephra { get; set; } = "tephra.json";
    public string Reservoir { get; set; } = "reservoir.json";
    public string Exclusions { get; set; } = "exclusions.json";
}

public sealed class CaseStep
{
    /// <summary>import | reviseParameters | derive | decide | freeze | replaceSource |
    /// branch | concurrentFreeze | rollbackView</summary>
    public string Type { get; set; } = "";
    public string? Label { get; set; }
    public string? Branch { get; set; }
    public string? Revision { get; set; }
    public string? Document { get; set; }
    public string? AltFile { get; set; }
    public string[]? AffectedIds { get; set; }

    // parameter revision
    public string? VersionId { get; set; }
    public int? MonteCarloDraws { get; set; }
    public double? TieToleranceSigma { get; set; }
    public int? CandidateBudget { get; set; }
    public double? BandGridStepM { get; set; }

    // decisions / freeze
    public string? CandidateSelector { get; set; } // optimal:0 | optimal:1 | <id>
    public string? Action { get; set; }
    public string? Note { get; set; }

    // branching / rollback
    public string? NewBranchId { get; set; }
    public string? FromBranch { get; set; }
    public int? FromSeq { get; set; }
    public string? Reason { get; set; }

    // expectations asserted by the replay engine
    public StepExpectation? Expect { get; set; }
}

public sealed class StepExpectation
{
    public string? Status { get; set; }               // Ok | IncompleteInput | SemanticUnsolvable | BudgetExhausted
    public string[]? Findings { get; set; }           // finding kinds that must appear in this step
    public string[]? FindingsAbsent { get; set; }
    public int? CandidatesCount { get; set; }
    public int? OptimalCount { get; set; }
    public string[]? AffectedIds { get; set; }        // affected-object containment assertion
    public string? BlockedSection { get; set; }
    public bool? DecisionConflicts { get; set; }
    public string? RestoredParameterVersion { get; set; }
    public string? RestoredResultId { get; set; }
    public string? AdoptedCandidateId { get; set; }
    public bool? RejectionRecorded { get; set; }
}

public sealed class CounterExampleSpec
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The single direct source document changed; everything else stays identical.</summary>
    public string MutatedDocument { get; set; } = "";
    public CaseStep Mutation { get; set; } = new();
    public StepExpectation Expect { get; set; } = new();
    public string Judgment { get; set; } = "";
}
