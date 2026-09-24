using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgeDepth.Domain;

namespace AgeDepth.Engine;

public sealed record EngineInput(
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<Gap> Gaps);

public static partial class AgeDepthEngine
{
    public static ModelResult Build(EngineInput input, ModelParameters p)
    {
        var valid = new List<Diagnostic>();
        if (input.Segments.Count == 0)
            valid.Add(new Diagnostic { Code = "NO_SEGMENTS", Severity = "error",
                Message = "at least one segment is required", Position = "$.segments" });
        if (input.Anchors.Count == 0 && input.EventLayers.Count == 0)
            valid.Add(new Diagnostic { Code = "NO_AGE_CONTROL", Severity = "error",
                Message = "at least one age anchor or event layer is required", Position = "$" });

        var segById = new Dictionary<string, Segment>();
        var seenSeg = new HashSet<string>();
        foreach (var seg in input.Segments)
        {
            if (!seenSeg.Add(seg.Id))
                valid.Add(new Diagnostic { Code = "DUP_SEGMENT_ID", Severity = "error",
                    Message = $"duplicate segment id '{seg.Id}'", Position = "$.segments", ObjectId = seg.Id });
            segById[seg.Id] = seg;
            CheckDepth(seg.TopDepth, $"segments[{seg.Id}].topDepth", seg.Id, valid);
            CheckDepth(seg.BottomDepth, $"segments[{seg.Id}].bottomDepth", seg.Id, valid);
            if (seg.TopDepth.IsUsable() && seg.BottomDepth.IsUsable()
                && seg.BottomDepth.Value!.Value < seg.TopDepth.Value!.Value)
                valid.Add(new Diagnostic { Code = "SEGMENT_RANGE_REVERSED", Severity = "error",
                    Message = $"segment '{seg.Id}' bottom above top",
                    Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });
        }

        var segRanges = input.Segments
            .Where(s => s.TopDepth.IsUsable() && s.BottomDepth.IsUsable())
            .OrderBy(s => s.TopDepth.Value!.Value)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        for (var i = 1; i < segRanges.Count; i++)
        {
            if (segRanges[i - 1].BottomDepth.Value!.Value > segRanges[i].TopDepth.Value!.Value
                && !input.Gaps.Any(g =>
                    (g.AboveSegmentId == segRanges[i - 1].Id && g.BelowSegmentId == segRanges[i].Id) ||
                    (g.AboveSegmentId == segRanges[i].Id && g.BelowSegmentId == segRanges[i - 1].Id)))
                valid.Add(new Diagnostic
                {
                    Code = "OVERLAPPING_SEGMENTS", Severity = "error",
                    Message = $"segments '{segRanges[i - 1].Id}' and '{segRanges[i].Id}' overlap without an explicit gap",
                    Position = "$.segments",
                    ObjectId = $"{segRanges[i - 1].Id}|{segRanges[i].Id}"
                });
        }

        var excludedTargets = input.Exclusions
            .Where(x => !x.Reinstated)
            .Select(x => x.TargetId)
            .ToHashSet();

        var controls = new List<AgeControl>();
        foreach (var a in input.Anchors)
        {
            CheckDepth(a.Depth, $"anchors[{a.Id}].depth", a.Id, valid);
            if (a.AgeSamples.Count == 0)
                valid.Add(new Diagnostic { Code = "EMPTY_DISTRIBUTION", Severity = "error",
                    Message = $"anchor '{a.Id}' has no age samples",
                    Position = $"$.anchors[{a.Id}].ageSamples", ObjectId = a.Id });
            if (a.AgeSamples.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                valid.Add(new Diagnostic { Code = "NON_FINITE_AGE", Severity = "error",
                    Message = $"anchor '{a.Id}' distribution contains non-finite ages",
                    Position = $"$.anchors[{a.Id}].ageSamples", ObjectId = a.Id });
            if (!segById.ContainsKey(a.SegmentId))
                valid.Add(new Diagnostic { Code = "UNKNOWN_SEGMENT", Severity = "error",
                    Message = $"anchor '{a.Id}' references unknown segment '{a.SegmentId}'",
                    Position = $"$.anchors[{a.Id}].segmentId", ObjectId = a.Id });
            if (a.Depth.IsUsable() && a.AgeSamples.Count > 0 && segById.ContainsKey(a.SegmentId))
                controls.Add(new AgeControl(a.Id, a.SegmentId, a.Depth.Value!.Value,
                    a.AgeSamples.OrderBy(x => x).ToList(),
                    a.ReservoirOffsetId is { } rid
                        && input.ReservoirOffsets.FirstOrDefault(r => r.Id == rid) is { } off
                        ? off.AgeSamples.OrderBy(x => x).ToList() : null,
                    excludedTargets.Contains(a.Id), "anchor"));
        }
        foreach (var e in input.EventLayers)
        {
            CheckDepth(e.Depth, $"eventLayers[{e.Id}].depth", e.Id, valid);
            if (e.AgeSamples.Count == 0)
                valid.Add(new Diagnostic { Code = "EMPTY_DISTRIBUTION", Severity = "error",
                    Message = $"event layer '{e.Id}' has no age samples",
                    Position = $"$.eventLayers[{e.Id}].ageSamples", ObjectId = e.Id });
            var seg = LocateSegment(e.Depth, input.Segments, input.Gaps);
            if (e.Depth.IsUsable() && e.AgeSamples.Count > 0 && seg is not null)
                controls.Add(new AgeControl(e.Id, seg.Id, e.Depth.Value!.Value,
                    e.AgeSamples.OrderBy(x => x).ToList(), null,
                    excludedTargets.Contains(e.Id), "event"));
            else if (e.Depth.IsUsable() && seg is null)
                valid.Add(new Diagnostic { Code = "EVENT_ON_BOUNDARY", Severity = "warning",
                    Message = $"event layer '{e.Id}' sits on a gap/unconformity boundary and is not used inside any segment",
                    Position = $"$.eventLayers[{e.Id}]", ObjectId = e.Id });
        }

        var hardErrors = valid.Where(d => d.Severity == "error").ToList();
        var inputHash = HashInput(input, p);
        if (hardErrors.Count > 0)
            return new ModelResult { Status = ComputeStatus.CorruptInput,
                ParametersVersion = p.Version, InputHash = inputHash,
                Diagnostics = hardErrors.Concat(valid.Where(d => d.Severity != "error")).ToList() };

        var segModels = new List<SegmentModel>();
        var anyUnsolvable = false;
        foreach (var seg in input.Segments.OrderBy(s => s.TopDepth.Value!.Value)
                     .ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var model = BuildSegment(seg, controls, p, valid);
            segModels.Add(model);
            if (!model.Solved) anyUnsolvable = true;
        }

        var allDiag = valid.OrderBy(d => d.Position, StringComparer.Ordinal).ToList();
        var worst = anyUnsolvable
            ? (segModels.Any(s => s.Solved) ? ComputeStatus.Unsolvable : ComputeStatus.Unsolvable)
            : ComputeStatus.Ok;
        return new ModelResult
        {
            Status = segModels.All(s => !s.Solved) && anyUnsolvable ? ComputeStatus.Unsolvable : worst,
            ParametersVersion = p.Version,
            InputHash = inputHash,
            Diagnostics = allDiag,
            Segments = segModels,
            AcceptedDraws = segModels.Sum(s => s.Candidates.Count),
            AttemptedDraws = segModels.Sum(s => Math.Max(s.Candidates.Count, 0))
        };
    }

    private static void CheckDepth(Quantity q, string position, string objectId, List<Diagnostic> diag)
    {
        if (q.Diagnostic is { } d)
            diag.Add(new Diagnostic { Code = "UNCONVERTIBLE_VALUE", Severity = "error",
                Message = d, Position = position, ObjectId = objectId });
        else if (q.Value!.Value < 0)
            diag.Add(new Diagnostic { Code = "NEGATIVE_DEPTH", Severity = "error",
                Message = $"depth is negative: '{q.Raw}'", Position = position, ObjectId = objectId });
    }

    private static Segment? LocateSegment(Quantity depth, IReadOnlyList<Segment> segments, IReadOnlyList<Gap> gaps)
    {
        if (!depth.IsUsable()) return null;
        var d = depth.Value!.Value;
        foreach (var g in gaps)
            if (g.Depth.IsUsable() && Math.Abs(g.Depth.Value!.Value - d) < 1e-9) return null;
        return segments.FirstOrDefault(s => s.TopDepth.IsUsable() && s.BottomDepth.IsUsable()
            && d >= s.TopDepth.Value!.Value - 1e-9 && d <= s.BottomDepth.Value!.Value + 1e-9);
    }

    private static string HashInput(EngineInput input, ModelParameters p)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            segments = input.Segments.OrderBy(s => s.Id).Select(s => new { s.Id, s.TopDepth, s.BottomDepth, s.IsInverted }),
            anchors = input.Anchors.OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.SegmentId, depth = a.Depth.Value, samples = a.AgeSamples, a.ReservoirOffsetId, a.Excluded }),
            events = input.EventLayers.OrderBy(e => e.Id)
                .Select(e => new { e.Id, depth = e.Depth.Value, samples = e.AgeSamples }),
            gaps = input.Gaps.OrderBy(g => g.Id).Select(g => new { g.Id, g.AboveSegmentId, g.BelowSegmentId, depth = g.Depth.Value, g.Kind }),
            exclusions = input.Exclusions.OrderBy(x => x.Id).Select(x => new { x.Id, x.TargetId, x.Reinstated }),
            p
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }
}
