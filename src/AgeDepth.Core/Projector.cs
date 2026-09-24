using System.Text.Json;

namespace AgeDepth.Core;

public sealed record DecisionRecord(long Seq, string Kind, string TargetId, string Verdict, string Rationale, string At);
public sealed record ConflictRecord(long Seq, string Kind, string Detail, string At);

public sealed record ReviewState(
    MaterialsBundle Materials,
    SegmentationResult Segmentation,
    List<ModelCandidate> Candidates,
    string? FrozenCandidateId,
    long? FrozenAtSeq,
    List<DecisionRecord> Decisions,
    List<ConflictRecord> Conflicts,
    List<Diagnostic> Diagnostics,
    long HeadSeq,
    ModelParams Params)
{
    public IEnumerable<ModelCandidate> CandidatesFor(string segmentId) =>
        Candidates.Where(c => c.SegmentId == segmentId);
}

/// <summary>事件 → 当前状态。任意 seq 截断重放即恢复当时的依赖版本与可见结论。</summary>
public static class Projector
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ReviewState Project(IReadOnlyList<EventRecord> events)
    {
        var materials = new MaterialsBundle(new(), new(), new(), new());
        var segmentation = new SegmentationResult(new(), new());
        var candidates = new List<ModelCandidate>();
        var decisions = new List<DecisionRecord>();
        var conflicts = new List<ConflictRecord>();
        var diagnostics = new List<Diagnostic>();
        string? frozenId = null; long? frozenSeq = null;
        var p = new ModelParams();

        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case EventKinds.MaterialsImported:
                {
                    var pl = e.PayloadObj();
                    materials = pl.GetProperty("materials").Deserialize<MaterialsBundle>(Json)!;
                    segmentation = pl.GetProperty("segmentation").Deserialize<SegmentationResult>(Json)!;
                    diagnostics.RemoveAll(d => d.Code is not "DECISION");
                    diagnostics.AddRange(segmentation.Diagnostics);
                    foreach (var d in pl.GetProperty("diagnostics").EnumerateArray())
                        diagnostics.Add(d.Deserialize<Diagnostic>(Json)!);
                    break;
                }
                case EventKinds.AnchorAdded:
                {
                    var anchor = e.PayloadObj().GetProperty("anchor").Deserialize<AgeAnchor>(Json)!;
                    materials.Anchors.RemoveAll(a => a.Id == anchor.Id);
                    materials.Anchors.Add(anchor);
                    break;
                }
                case EventKinds.AnchorStatus:
                {
                    var pl = e.PayloadObj();
                    var id = pl.GetProperty("anchorId").GetString()!;
                    var status = pl.GetProperty("status").GetString()!;
                    var reason = pl.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    var idx = materials.Anchors.FindIndex(a => a.Id == id);
                    if (idx >= 0)
                        materials.Anchors[idx] = materials.Anchors[idx] with { Status = status, ExclusionReason = reason };
                    break;
                }
                case EventKinds.ParamRevised:
                {
                    var pl = e.PayloadObj();
                    p = pl.GetProperty("params").Deserialize<ModelParams>(Json)!;
                    break;
                }
                case EventKinds.ModelComputed:
                {
                    var pl = e.PayloadObj();
                    var affected = pl.GetProperty("affectedSegments").Deserialize<string[]>(Json)!;
                    candidates.RemoveAll(c => affected.Contains(c.SegmentId));
                    foreach (var c in pl.GetProperty("candidates").EnumerateArray())
                        candidates.Add(c.Deserialize<ModelCandidate>(Json)!);
                    candidates = candidates
                        .OrderBy(c => c.SegmentId, StringComparer.Ordinal)
                        .ThenByDescending(c => c.Score)
                        .ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
                    foreach (var d in pl.GetProperty("diagnostics").EnumerateArray())
                        diagnostics.Add(d.Deserialize<Diagnostic>(Json)!);
                    break;
                }
                case EventKinds.ModelFrozen:
                    frozenId = e.PayloadObj().GetProperty("candidateId").GetString();
                    frozenSeq = e.Seq;
                    break;
                case EventKinds.ModelFreezeRejected:
                    conflicts.Add(new ConflictRecord(e.Seq, e.Kind,
                        e.PayloadObj().GetProperty("reason").GetString() ?? "", e.At));
                    break;
                case EventKinds.DecisionRecorded:
                {
                    var pl = e.PayloadObj();
                    decisions.Add(new DecisionRecord(e.Seq,
                        pl.GetProperty("kind").GetString()!,
                        pl.GetProperty("targetId").GetString()!,
                        pl.GetProperty("verdict").GetString()!,
                        pl.GetProperty("rationale").GetString()!, e.At));
                    break;
                }
            }
        }
        return new ReviewState(materials, segmentation, candidates, frozenId, frozenSeq,
            decisions, conflicts, diagnostics, events.Count == 0 ? 0 : events[^1].Seq, p);
    }

    public static JsonElement PayloadObj(this EventRecord e) =>
        JsonDocument.Parse(e.Payload).RootElement;
}
