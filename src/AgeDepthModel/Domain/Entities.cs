namespace AgeDepthModel.Domain;

public enum SegmentKind { Continuous, Gap, Unconformity }

public enum AnchorStatus { Adopted, Rejected }

public enum CandidateStatus { Active, Frozen, Superseded }

public enum DecisionKind { Accept, Reject, ParamAdjust, Exclusion }

/// <summary>Ok 之外，资源耗尽 / 格式损坏 / 语义无解 / 并发冲突必须互相区分。</summary>
public enum ResultStatus { Ok, ResourceExhausted, CorruptFormat, SemanticUnsolvable, Conflict }

/// <summary>带位置的诊断：定位到具体对象与字段，不允许用空值伪装成功。</summary>
public sealed record Diagnostic(string Code, string Message, string Location);

public sealed record CoreRecord(string Id, string Name, string DepthDatum, string DepthUnit);

public sealed record Segment(
    string Id, string CoreId, int Seq, double Top, double Base,
    SegmentKind Kind, string Label);

public sealed record Anchor(
    string Id, string SegmentId, int Seq, double Depth, string DepthUnit,
    string RawAge, string AgeUnit, string DistributionJson,
    double? ReservoirOffsetYears, string Source,
    AnchorStatus Status, string? ExclusionReason);

public sealed record EventLayer(
    string Id, string SegmentId, double Depth, string Name, string Correlation);

public sealed record Candidate(
    string Id, string Name, string? ParentId, string ParameterVersion,
    CandidateStatus Status, int Version, long CreatedSeq);

public sealed record Decision(
    string Id, DecisionKind Kind, string TargetId, string PayloadJson,
    long EventSeq, bool Superseded);

public sealed record JournalEvent(long Seq, string Type, string PayloadJson, string? SnapshotJson);
