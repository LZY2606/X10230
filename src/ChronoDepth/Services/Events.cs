using ChronoDepth.Domain;
using ChronoDepth.Engine;

namespace ChronoDepth.Services;

public static class EventTypes
{
    public const string MaterialsImported = "materials-imported";
    public const string AnchorAdded = "anchor-added";
    public const string ParamsRevised = "params-revised";
    public const string ExclusionDecided = "exclusion-decided";
    public const string ModelsComputed = "models-computed";
    public const string ModelFrozen = "model-frozen";
    public const string ReviewDecided = "review-decided";
    public const string JudgmentRecorded = "judgment-recorded";
}

public sealed record MaterialsImportedPayload(string CaseId, string CaseTitle, MaterialSet Materials);
public sealed record AnchorAddedPayload(string CaseId, Anchor Anchor);
public sealed record ParamsRevisedPayload(string CaseId, EngineParams Params, string Reason);
public sealed record ExclusionPayload(string CaseId, ExclusionDecision Decision);
public sealed record FreezePayload(string CaseId, string ModelId, string By);

public sealed record ReviewDecision(string ModelId, string Verdict, string Note, string By);
public sealed record ReviewPayload(string CaseId, ReviewDecision Decision);

/// <summary>Machine-readable judgment basis (e.g. monotonicity rules).</summary>
public sealed record Judgment(string Code, string Subject, string Basis, string Outcome);
public sealed record JudgmentPayload(string CaseId, Judgment Judgment);

public sealed record ModelsComputedPayload(
    string CaseId,
    List<CandidateModel> Models,
    List<Diagnostic> Diagnostics,
    string ResultKind,
    string InputDigest,
    string ParamVersion,
    Dictionary<string, string> RunDigests);
