using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;

namespace Chrono.Storage.Workflow;

public sealed record ViewAnchor(
    string Id,
    string Label,
    string Role,
    double DepthMeters,
    string DepthRawText,
    string DepthDisplay,
    string DistributionType,
    double Q025,
    double Q500,
    double Q975,
    bool LongTail,
    string? ReservoirId,
    string? EventBedId,
    string? DuplicateGroupId,
    string? TieGroupId,
    string Provenance,
    bool Excluded,
    string? ExclusionReason,
    string? RawConventionalBp,
    string? RawSigmaBp,
    string LabCode);

public sealed record ViewSection(string Id, string Name, int Index,
    double TopMeters, double BottomMeters, string DatumCode);

public sealed record ViewBoundary(string Id, double DepthMeters, string Kind,
    string Label, string RawDepthText);

public sealed record ViewEventBed(string Id, string Name, string Kind,
    double DepthMeters, double ThicknessMeters, string CorrelationKey,
    string RawDepthText, bool Unconverted);

public sealed record ViewRate(string Id, string CandidateId, string ChainId,
    int IntervalIndex, double LowerCmPerKa, double UpperCmPerKa);

public sealed record ViewBandPoint(string Id, string CandidateId,
    double DepthMeters, double Q025, double Q500, double Q975, bool LongTailDominant);

public sealed record ViewCandidate(
    string Id,
    string ChainId,
    string Family,
    int Rank,
    bool Tied,
    double Score,
    double CoveragePct,
    IReadOnlyList<ViewRate> Rates,
    IReadOnlyList<ViewBandPoint> Band,
    IReadOnlyList<string> AnchorIds);

public sealed record ViewChain(string Id, string Label, int Order,
    double TopMeters, double BottomMeters, bool Unbroken,
    IReadOnlyList<string> ViolationCodes, IReadOnlyList<string> AnchorIds);

public sealed record ViewJudgment(string Id, string RuleId, string RuleVersion,
    string Verdict, IReadOnlyList<string> SubjectIds, string Basis,
    IReadOnlyList<string> EvidenceEventIds, string EventId);

public sealed record ViewEvent(
    string Id,
    string Kind,
    long Sequence,
    string BranchId,
    string? Label,
    string Actor,
    string RuleSetVersion,
    string CreatedAtUtc,
    IReadOnlyList<string> ParentEventIds,
    bool Frozen);

public sealed record ViewDiagnostic(string Code, string Severity,
    string Position, string Message, string? RawSnippet);

public sealed record ViewRawToken(string Id, string SourceId, string Field,
    string RawText, string Status, string? CanonicalUnit, string? Diagnostic);

public sealed record ReviewModel(
    string ProjectId,
    string ProjectName,
    string AtEventId,
    string BranchId,
    IReadOnlyList<string> Branches,
    bool BranchFrozen,
    string TimeDatum,
    string VerticalDatum,
    string ParametersVersion,
    IReadOnlyList<ViewSection> Sections,
    IReadOnlyList<ViewBoundary> Boundaries,
    IReadOnlyList<ViewEventBed> EventBeds,
    IReadOnlyList<ViewAnchor> Anchors,
    IReadOnlyList<ViewChain> Chains,
    IReadOnlyList<ViewCandidate> Candidates,
    IReadOnlyList<ViewJudgment> Judgments,
    IReadOnlyList<ViewEvent> Events,
    IReadOnlyList<ViewDiagnostic> Diagnostics,
    IReadOnlyList<ViewRawToken> RawTokens,
    string BuildStatus,
    string BuildSummary,
    IReadOnlyList<string> FrozenCandidateIds);

public static class ReviewModelBuilder
{
    public static ReviewModel Build(DerivedSnapshot snap, string branchId,
        IReadOnlyList<string> branches, bool frozen)
    {
        var p = snap.Materials!;
        var build = snap.Build!;
        var excluded = new HashSet<string>(p.Exclusions.Select(e => e.TargetAnchorId));

        var anchors = p.Anchors
            .OrderBy(a => a.Depth.Meters).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a =>
            {
                var dist = a.CorrectedAge ?? a.MeasuredAge;
                var exc = p.Exclusions.FirstOrDefault(e => e.TargetAnchorId == a.Id);
                return new ViewAnchor(
                    a.Id, a.Label, a.Role.ToString(), a.Depth.Meters, a.RawDepthText,
                    a.Depth.Format(LengthUnit.Meter),
                    dist.Kind.ToString(),
                    dist.Quantile(0.025), dist.Quantile(0.5), dist.Quantile(0.975),
                    dist.IsLongTail(),
                    a.ReservoirId, a.EventBedId, a.DuplicateGroupId, a.TieGroupId,
                    a.ProvenanceSourceId, excluded.Contains(a.Id), exc?.Reason,
                    a.Radiometric?.ConventionalBpText, a.Radiometric?.SigmaText,
                    a.Radiometric?.LabCode ?? "");
            }).ToList();

        var candidates = build.Candidates.Select(c => new ViewCandidate(
            c.CandidateId, c.ChainId, c.Family, c.Rank, c.Tied, c.Score, c.CoveragePct,
            c.Rates.Select((r, i) => new ViewRate(
                $"rate-{c.CandidateId}-{i}", c.CandidateId, c.ChainId, i,
                r.LowerCmPerKa, r.UpperCmPerKa)).ToList(),
            c.Band.Select((b, i) => new ViewBandPoint(
                $"band-{c.CandidateId}-{i}", c.CandidateId,
                b.DepthMeters, b.Q025CalBp, b.Q500CalBp, b.Q975CalBp, b.LongTailDominant)).ToList(),
            c.AnchorIds)).ToList();

        var judgments = snap.History
            .Where(e => e.Kind == EventKind.ModelComputed)
            .SelectMany(e =>
            {
                var payload = e.Payload<ComputedModelPayload>();
                return payload.Judgments.Select((j, i) => new ViewJudgment(
                    $"judgment-{e.Id}-{i}", j.RuleId, j.RuleVersion, j.Verdict,
                    j.SubjectIds, j.Basis, j.EvidenceEventIds, e.Id));
            }).ToList();

        var events = snap.History.OrderBy(e => e.Sequence).Select(e => new ViewEvent(
            e.Id, e.Kind.ToString(), e.Sequence, e.BranchId, e.Label,
            e.Actor, e.RuleSetVersion, e.CreatedAtUtc.ToString("O"),
            e.ParentEventIds, e.Frozen)).ToList();

        var diagnostics = build.Report.Diagnostics.Select(d => new ViewDiagnostic(
            d.Code, d.Severity.ToString(), d.Position, d.Message, d.RawSnippet)).ToList();

        var unconvertedSource = new HashSet<string>(p.RawTokens
            .Where(t => t.Status != RawParseStatus.Parsed)
            .Select(t => t.SourceId + ":" + t.Field));

        return new ReviewModel(
            p.Id, p.Name, snap.AtEventId, branchId, branches, frozen,
            p.DefaultTimeDatum.Code, "CSF-A (m, core-top zero)",
            snap.Parameters.Version,
            p.Sections.Select(s => new ViewSection(s.Id, s.Name, s.SectionIndex,
                s.Top.Meters, s.Bottom.Meters, s.Datum.Code)).ToList(),
            p.Boundaries.Select(b => new ViewBoundary(b.Id, b.Depth.Meters,
                b.Kind.ToString(), b.Label, b.RawDepthText)).ToList(),
            p.EventBeds.Select(b => new ViewEventBed(b.Id, b.Name, b.Kind.ToString(),
                b.Depth.Meters, b.ThicknessMeters, b.CorrelationKey, b.RawDepthText,
                unconvertedSource.Contains(b.Id + ":depth"))).ToList(),
            anchors,
            build.Chains.Select(c => new ViewChain(c.ChainId, c.Label, c.Order,
                c.TopMeters, c.BottomMeters, c.Unbroken, c.ViolationCodes,
                c.Anchors.Select(a => a.Id).ToList())).ToList(),
            candidates, judgments, events, diagnostics,
            p.RawTokens.Select((t, i) => new ViewRawToken(
                $"raw-{i}-{t.SourceId}-{t.Field}", t.SourceId, t.Field,
                t.RawText, t.Status.ToString(), t.CanonicalUnit, t.Diagnostic)).ToList(),
            build.Status.ToString(), build.Report.Summary,
            snap.FrozenCandidateIds);
    }
}
