using ChronoCore;
using ChronoStorage;

namespace ChronoWeb;

/// <summary>审阅页视图模型：每个显示值带稳定身份。</summary>
public sealed record ReviewModel(
    string BranchId,
    long AtSeq,
    ProjectState State,
    IReadOnlyList<ChronoEvent> Events,
    IReadOnlyList<ChronoRepository.BranchRow> Branches,
    IReadOnlyList<DerivedModel> Models,
    DerivedModel? Current,
    IReadOnlyList<ReplayStepResult> ReplayResults,
    IReadOnlyList<JudgmentPayload> Judgments,
    string? FlashKind,
    string? Flash);
