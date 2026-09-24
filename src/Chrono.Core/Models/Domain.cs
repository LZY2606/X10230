using Chrono.Core.Domain;

namespace Chrono.Core.Models;

public enum SegmentKind { Continuous, Gap, Unconformity, Inverted }

public enum AnchorStatus { Active, Excluded, TiedAlternative, FlaggedInversion, Unconvertible, Duplicate }

public sealed record DepthDatum(string Id, string Name, Qty Depth, string CoordinateBasis, bool PositiveDowncore, string Raw);

public sealed record CoreSegment(string Id, string Name, Qty Top, Qty Bottom, SegmentKind Kind, string Note, string Raw)
{
    public bool IsBarrier => Kind is SegmentKind.Gap or SegmentKind.Unconformity;
}

public sealed record ReservoirCorrection(string Id, string AnchorId, Qty OffsetYr, string Basis, string Raw);

public sealed record EventLayer(string Id, string Name, string? CorrelatedAnchorId, Qty? ExternalAge, string Basis, string Raw);

/// <summary>Exclusion decisions are first-class immutable records: every
/// decision to drop material is retained and auditable.</summary>
public sealed record ExclusionDecision(string Id, string TargetId, string Reason, bool Excluded, string DecidedBy, string Raw);

public sealed record AgeAnchor(
    string Id, string Label, Qty Depth, AgeDistribution Distribution,
    string Material, string AgeBasis, string? ReservoirId, string? EventId,
    AnchorStatus Status, string? ExclusionId, IReadOnlyList<Diagnostic> Diagnostics, string Raw, int Revision = 1)
{
    public bool Usable => Status == AnchorStatus.Active || Status == AnchorStatus.TiedAlternative;
}
