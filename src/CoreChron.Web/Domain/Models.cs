using System.Text.Json.Serialization;

namespace CoreChron.Domain;

// ===== 带原始表示的量值：原始字符串永不丢弃，规范化只发生在派生层 =====
public sealed record Quantity(
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("datum")] string? Datum,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic = null)
{
    public bool IsConvertible => Value.HasValue && string.IsNullOrEmpty(Diagnostic);
}

public sealed record DepthPoint(
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("cm")] double? Cm,
    [property: JsonPropertyName("datum")] string? Datum,
    [property: JsonPropertyName("diagnostic")] string? Diagnostic = null)
{
    public bool IsConvertible => Cm.HasValue && string.IsNullOrEmpty(Diagnostic);
}

// ===== 放射性年龄分布：显式分布，禁止用均值替代 =====
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalAge), typeDiscriminator: "normal")]
[JsonDerivedType(typeof(LogNormalAge), typeDiscriminator: "lognormal")]
[JsonDerivedType(typeof(MonteCarloAge), typeDiscriminator: "samples")]
[JsonDerivedType(typeof(UncalibratedRadiocarbonAge), typeDiscriminator: "c14")]
[JsonDerivedType(typeof(SummaryAge), typeDiscriminator: "summary")]
public abstract record AgeDistribution
{
    public abstract string Describe();
}

public sealed record NormalAge(
    [property: JsonPropertyName("mean_a")] double MeanA,
    [property: JsonPropertyName("sigma_a")] double SigmaA) : AgeDistribution
{
    public override string Describe() => $"N({MeanA}, {SigmaA}^2) a";
}

public sealed record LogNormalAge(
    [property: JsonPropertyName("mu")] double Mu,
    [property: JsonPropertyName("sigma")] double Sigma) : AgeDistribution
{
    public override string Describe() => $"LogNormal(mu={Mu}, sigma={Sigma}) a";
}

public sealed record MonteCarloAge(
    [property: JsonPropertyName("samples_a")] IReadOnlyList<double> SamplesA) : AgeDistribution
{
    public override string Describe() => $"MC({SamplesA.Count} samples) a";
}

public sealed record UncalibratedRadiocarbonAge(
    [property: JsonPropertyName("bp_14c")] double Bp14C,
    [property: JsonPropertyName("sigma_14c")] double Sigma14C) : AgeDistribution
{
    public override string Describe() => $"{Bp14C} ± {Sigma14C} 14C BP";
}

// 无法转换 / 仅摘要（保留在原位置并附诊断），不参与派生计算
public sealed record SummaryAge(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("diagnostic")] string Diagnostic) : AgeDistribution
{
    public override string Describe() => Text;
}

// ===== 不可变材料（来自来源，改动产生新版本）=====
public sealed record DepthDatum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("top_cm")] double TopCm,
    [property: JsonPropertyName("bottom_cm")] double BottomCm,
    [property: JsonPropertyName("datum")] string Datum,
    [property: JsonPropertyName("raw")] string Raw);

public sealed record CoreBoundary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("depth")] DepthPoint Depth,
    [property: JsonPropertyName("kind")] string Kind, // conformable | unconformity | hiatal | fault
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("note")] string? Note = null);

public sealed record ReservoirOffset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("offset_a")] Quantity OffsetA,
    [property: JsonPropertyName("applies_to")] IReadOnlyList<string> AppliesTo,
    [property: JsonPropertyName("source")] string Source);

public sealed record EventBed(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("depth")] DepthPoint Depth,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("age")] AgeDistribution? Age,
    [property: JsonPropertyName("correlation")] string Correlation,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("raw")] string Raw);

public sealed record AgeAnchor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("depth")] DepthPoint Depth,
    [property: JsonPropertyName("age")] AgeDistribution Age,
    [property: JsonPropertyName("method")] string Method, // 14C | biostrat | tephra | luminescence
    [property: JsonPropertyName("event_id")] string? EventId,
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("note")] string? Note = null);

// ===== 可修改决定（历史全部保留，按事件版本化）=====
public enum DecisionKind { ExcludeAnchor, ExcludeEvent, ChooseCandidates, RejectCandidates, ParameterRevision, Note }

public sealed record DecisionRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] DecisionKind Kind,
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("created_utc")] DateTime CreatedUtc,
    [property: JsonPropertyName("active")] bool Active);

// ===== 规则版本与参数版本（机器可读判据）=====
public sealed record ChronologyParameters(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("interpolation")] string Interpolation, // linear | pava
    [property: JsonPropertyName("tie_strategy")] string TieStrategy,   // keep_all | merge_pooled
    [property: JsonPropertyName("monte_carlo_draws")] int MonteCarloDraws,
    [property: JsonPropertyName("max_candidate_combos")] int MaxCandidateCombos,
    [property: JsonPropertyName("reservoir_applied")] bool ReservoirApplied,
    [property: JsonPropertyName("note")] string? Note = null);

public static class RuleBook
{
    // 硬规则身份：复核界面与事件都引用这些稳定代码
    public const string Monotonicity = "P-MONO-01";   // 年龄只能在不断层段中随深度单调（变老）
    public const string ExplicitGap  = "P-GAP-01";    // 深度缺口与不整合是显式边界
    public const string Distribution = "P-DIST-01";   // 一个锚点的分布不能被均值替代
    public const string KeepTied     = "P-EQUI-01";   // 多个等优模型需保留
}

// ===== 诊断：带位置，绝不以空值/默认伪装成功 =====
public enum DiagnosticSeverity { Error, Warning, Info }
public sealed record Diagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity,
    [property: JsonPropertyName("where")] string Where,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("target_id")] string? TargetId = null);

// ===== 引擎结果 =====
public enum OutcomeStatus { Ok, SemanticImpasse, BudgetExhausted }

public sealed record CandidateRate(
    [property: JsonPropertyName("block_id")] string BlockId,
    [property: JsonPropertyName("top_cm")] double TopCm,
    [property: JsonPropertyName("bottom_cm")] double BottomCm,
    [property: JsonPropertyName("rate_cm_per_ka")] double RateCmPerKa,
    [property: JsonPropertyName("q025")] double Q025,
    [property: JsonPropertyName("q975")] double Q975);

public sealed record ModelCandidate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("tie_choices")] IReadOnlyList<string> TieChoices,
    [property: JsonPropertyName("violations")] IReadOnlyList<string> Violations,
    [property: JsonPropertyName("feasible")] bool Feasible,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("rates")] IReadOnlyList<CandidateRate> Rates);

public sealed record AgeBandPoint(
    [property: JsonPropertyName("depth_cm")] double DepthCm,
    [property: JsonPropertyName("q025_a")] double Q025A,
    [property: JsonPropertyName("median_a")] double MedianA,
    [property: JsonPropertyName("q975_a")] double Q975A,
    [property: JsonPropertyName("valid")] bool Valid);

public sealed record ChronologyBlock(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("top_cm")] double TopCm,
    [property: JsonPropertyName("bottom_cm")] double BottomCm,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<string> AnchorIds,
    [property: JsonPropertyName("bounded_by")] IReadOnlyList<string> BoundedBy);

public sealed record ChronologyResult(
    [property: JsonPropertyName("status")] OutcomeStatus Status,
    [property: JsonPropertyName("parameters_version")] string ParametersVersion,
    [property: JsonPropertyName("input_digest")] string InputDigest,
    [property: JsonPropertyName("blocks")] IReadOnlyList<ChronologyBlock> Blocks,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ModelCandidate> Candidates,
    [property: JsonPropertyName("band")] IReadOnlyList<AgeBandPoint> Band,
    [property: JsonPropertyName("calibrated_anchors")] IReadOnlyList<CalibratedAnchorView> CalibratedAnchors,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CalibratedAnchorView(
    [property: JsonPropertyName("anchor_id")] string AnchorId,
    [property: JsonPropertyName("depth_cm")] double DepthCm,
    [property: JsonPropertyName("q025_a")] double Q025A,
    [property: JsonPropertyName("median_a")] double MedianA,
    [property: JsonPropertyName("q975_a")] double Q975A,
    [property: JsonPropertyName("calibration")] string Calibration,
    [property: JsonPropertyName("reservoir_delta_a")] double? ReservoirDeltaA);

// ===== 事件（机器可读，含输入摘要/参数版本/父依赖）=====
public enum EventType
{
    ProjectCreated, MaterialImported, ParameterRevision,
    AnchorAdded, AnchorExcluded, AnchorReinstated,
    EventExcluded, EventReinstated, TieDecision,
    Freeze, Unfreeze, NewAnchorCompared, Rewind,
    ScenarioReplayed, Diagnostic
}

public sealed record ChronEvent(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("type")] EventType Type,
    [property: JsonPropertyName("created_utc")] DateTime CreatedUtc,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("input_digest")] string? InputDigest,
    [property: JsonPropertyName("parameters_version")] string? ParametersVersion,
    [property: JsonPropertyName("parent_event_ids")] IReadOnlyList<string> ParentEventIds,
    [property: JsonPropertyName("payload")] System.Text.Json.Nodes.JsonNode? Payload,
    [property: JsonPropertyName("derived_result")] ChronologyResult? DerivedResult);

// ===== 分支与冻结 =====
public sealed record BranchState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("head_seq")] long HeadSeq,
    [property: JsonPropertyName("frozen_event_id")] string? FrozenEventId,
    [property: JsonPropertyName("frozen_candidate_id")] string? FrozenCandidateId);

// ===== 完整项目快照（状态）=====
public sealed record ProjectState(
    [property: JsonPropertyName("project")] ProjectInfo Project,
    [property: JsonPropertyName("depth_data")] IReadOnlyList<DepthDatum> DepthData,
    [property: JsonPropertyName("boundaries")] IReadOnlyList<CoreBoundary> Boundaries,
    [property: JsonPropertyName("anchors")] IReadOnlyList<AgeAnchor> Anchors,
    [property: JsonPropertyName("reservoirs")] IReadOnlyList<ReservoirOffset> Reservoirs,
    [property: JsonPropertyName("events_beds")] IReadOnlyList<EventBed> EventsBeds,
    [property: JsonPropertyName("calibration_curve")] CalibrationCurve? CalibrationCurve,
    [property: JsonPropertyName("parameters")] ChronologyParameters Parameters,
    [property: JsonPropertyName("decisions")] IReadOnlyList<DecisionRecord> Decisions);

public sealed record ProjectInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("core_name")] string CoreName,
    [property: JsonPropertyName("depth_datum")] string DepthDatum,
    [property: JsonPropertyName("time_datum")] string TimeDatum,
    [property: JsonPropertyName("created_utc")] DateTime CreatedUtc,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record CalibrationPoint(
    [property: JsonPropertyName("cal_bp")] double CalBP,
    [property: JsonPropertyName("c14_bp")] double C14BP);

public sealed record CalibrationCurve(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("points")] IReadOnlyList<CalibrationPoint> Points);
