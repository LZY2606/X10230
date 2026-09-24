using System.Text.Json;

namespace AgeDepth.Core;

// ---- 导入结果：预算耗尽 / 格式损坏 / 语义无解 是三种不同结果 ----
public abstract record ImportOutcome;
public sealed record ImportOk(long EventSeq, int DiagnosticCount) : ImportOutcome;
public sealed record BudgetExhausted(string Detail) : ImportOutcome;
public sealed record FormatCorrupt(string Detail, string Position) : ImportOutcome;
public sealed record SemanticUnsolvable(IReadOnlyList<Diagnostic> Diagnostics) : ImportOutcome;

public abstract record FreezeOutcome;
public sealed record Frozen(EventRecord Event) : FreezeOutcome;
public sealed record FreezeConflict(long Expected, long Actual, EventRecord RejectEvent) : FreezeOutcome;
public sealed record FreezeTargetMissing(string CandidateId) : FreezeOutcome;

public sealed record ImpactReport(
    string SegmentId, string? FrozenCandidateId, string[] NewCandidateIds,
    double? MaxMedianShiftKa, double? MaxBandWidthChangeKa, string Summary);

public sealed class Engine
{
    private readonly EventStore _store;
    private readonly Random _rng;
    public const int MaxEntitiesPerImport = 2000;

    public Engine(EventStore store, int? seed = 42)
    {
        _store = store;
        _rng = seed is { } s ? new Random(s) : new Random();
    }

    public ReviewState State(long? atSeq = null) => Projector.Project(_store.Read(atSeq));
    public IReadOnlyList<EventRecord> Events(long? upTo = null) => _store.Read(upTo);
    public long HeadSeq => _store.HeadSeq;

    // ---- 导入材料（不可变层）----

    public ImportOutcome ImportMaterialsJson(string json)
    {
        MaterialsBundle bundle;
        try { bundle = MaterialsJson.Parse(json); }
        catch (FormatCorruptException ex) { return new FormatCorrupt(ex.Message, ex.Position); }
        return ImportMaterials(bundle);
    }

    public ImportOutcome ImportMaterials(MaterialsBundle materials)
    {
        var entityCount = materials.Sections.Count + materials.Boundaries.Count
                        + materials.Anchors.Count + materials.EventLayers.Count;
        if (entityCount > MaxEntitiesPerImport)
            return new BudgetExhausted($"实体数 {entityCount} 超过单次导入预算 {MaxEntitiesPerImport}");

        var seg = Segmenter.Build(materials.Sections, materials.Boundaries);
        var diags = new List<Diagnostic>(seg.Diagnostics);
        diags.AddRange(materials.Sections.SelectMany(s => new[] { s.Top.Issue, s.Bottom.Issue }.Where(i => i is not null))!);
        diags.AddRange(materials.Anchors.SelectMany(a => new[] { a.Depth.Issue, a.ReservoirOffset?.Issue }.Where(i => i is not null))!);

        if (seg.Segments.Count == 0)
            return new SemanticUnsolvable(diags.Concat(new[]
            {
                new Diagnostic("NO_VALID_SEGMENT", "materials", "没有任何可用分段，无法建立层段", "error")
            }).ToList());

        var p = State().Params;
        var inputHash = ModelBuilder.HashMaterials(materials, p.Version);
        var payload = JsonSerializer.Serialize(new
        {
            materials, segmentation = seg, diagnostics = diags
        }, Projector.Json);
        var evt = _store.Append(EventKinds.MaterialsImported, payload, Array.Empty<string>(), p.Version, inputHash);
        Recompute(null, "初始导入");
        return new ImportOk(evt.Seq, diags.Count);
    }

    // ---- 计算（派生层，局部重算）----

    public EventRecord Recompute(string[]? segmentIds, string reason)
    {
        var state = State();
        var affected = segmentIds ?? state.Segmentation.Segments.Select(s => s.Id).ToArray();
        var inputHash = ModelBuilder.HashMaterials(state.Materials, state.Params.Version);
        var allCandidates = new List<ModelCandidate>();
        var diags = new List<Diagnostic>();
        foreach (var segId in affected.OrderBy(x => x, StringComparer.Ordinal))
        {
            var seg = state.Segmentation.Segments.FirstOrDefault(s => s.Id == segId);
            if (seg is null) continue;
            var result = ModelBuilder.BuildSegment(seg, state.Materials.Anchors, state.Params,
                inputHash, new[] { $"seq:{state.HeadSeq}" }, _rng);
            allCandidates.AddRange(result.Candidates);
            diags.AddRange(result.Diagnostics);
            if (result.BudgetExhausted)
                diags.Add(new Diagnostic("BUDGET_EXHAUSTED", $"segment:{segId}", result.BudgetDetail!, "error"));
        }
        var payload = JsonSerializer.Serialize(new
        {
            affectedSegments = affected, candidates = allCandidates, diagnostics = diags, reason
        }, Projector.Json);
        return _store.Append(EventKinds.ModelComputed, payload,
            new[] { $"seq:{state.HeadSeq}" }, state.Params.Version, inputHash);
    }

    // ---- 冻结（并发检验）----

    public FreezeOutcome Freeze(string candidateId, long expectedSeq)
    {
        var state = State();
        if (state.Candidates.All(c => c.Id != candidateId))
            return new FreezeTargetMissing(candidateId);
        var payload = JsonSerializer.Serialize(new { candidateId }, Projector.Json);
        try
        {
            var evt = _store.Append(EventKinds.ModelFrozen, payload,
                new[] { $"seq:{expectedSeq}" }, state.Params.Version,
                ModelBuilder.HashMaterials(state.Materials, state.Params.Version), expectedSeq);
            return new Frozen(evt);
        }
        catch (ConcurrencyConflictException ex)
        {
            var reject = _store.Append(EventKinds.ModelFreezeRejected, JsonSerializer.Serialize(new
            {
                candidateId, expectedSeq, actualSeq = ex.Actual,
                reason = $"并发冻结冲突: 期望序列 {ex.Expected}，实际 {ex.Actual}；本次冻结被拒绝并保留记录"
            }, Projector.Json), new[] { $"seq:{ex.Expected}" }, state.Params.Version, "freeze-conflict");
            return new FreezeConflict(ex.Expected, ex.Actual, reject);
        }
    }

    // ---- 新锚点：局部重算 + 与冻结模型比较 ----

    public ImpactReport AddAnchor(AgeAnchor anchor)
    {
        var state = State();
        var payload = JsonSerializer.Serialize(new { anchor }, Projector.Json);
        _store.Append(EventKinds.AnchorAdded, payload, new[] { $"seq:{state.HeadSeq}" },
            state.Params.Version, ModelBuilder.HashMaterials(anchor, state.Params.Version));

        var after = State();
        var seg = anchor.Depth.Canonical is { } d
            ? after.Segmentation.Segments.FirstOrDefault(s => d >= s.TopM - 1e-9 && d <= s.BottomM + 1e-9)
            : null;
        if (seg is null)
            return new ImpactReport("", after.FrozenCandidateId, Array.Empty<string>(), null, null,
                $"锚点 {anchor.Id} 的深度无法定位到任何不断层段，已保留在档案中，未触发重算");

        Recompute(new[] { seg.Id }, $"新锚点 {anchor.Id} 局部重算");
        var now = State();
        var newCands = now.CandidatesFor(seg.Id).ToList();
        var frozen = now.Candidates.FirstOrDefault(c => c.Id == now.FrozenCandidateId);
        double? maxShift = null, maxWidth = null;
        if (frozen is not null && frozen.SegmentId == seg.Id && newCands.Count > 0)
        {
            var best = newCands.First();
            maxShift = frozen.Band.P50.Zip(best.Band.P50).Select(x => Math.Abs(x.First - x.Second)).Max();
            maxWidth = frozen.Band.P95.Zip(frozen.Band.P05).Zip(best.Band.P95.Zip(best.Band.P05))
                .Select(x => Math.Abs((x.First.First - x.First.Second) - (x.Second.First - x.Second.Second))).Max();
        }
        return new ImpactReport(seg.Id, now.FrozenCandidateId,
            newCands.Select(c => c.Id).ToArray(), maxShift, maxWidth,
            $"锚点 {anchor.Id} 落入 {seg.Id}，局部重算产生 {newCands.Count} 个候选" +
            (maxShift is { } ms ? $"；相对冻结模型最大中位偏移 {ms:0.###} ka，不确定度带宽变化 {maxWidth:0.###} ka" : ""));
    }

    // ---- 人工决定（可修改层，全部留痕）----

    public EventRecord Decide(string kind, string targetId, string verdict, string rationale)
    {
        var state = State();
        var evt = _store.Append(EventKinds.DecisionRecorded, JsonSerializer.Serialize(new
        { kind, targetId, verdict, rationale }, Projector.Json),
            new[] { $"seq:{state.HeadSeq}" }, state.Params.Version,
            ModelBuilder.HashMaterials(new { kind, targetId, verdict }, state.Params.Version));

        if (kind is "exclude-anchor" or "include-anchor")
        {
            var status = kind == "exclude-anchor" ? "excluded" : "accepted";
            _store.Append(EventKinds.AnchorStatus, JsonSerializer.Serialize(new
            { anchorId = targetId, status, reason = rationale }, Projector.Json),
                new[] { evt.Id }, state.Params.Version, "decision");
            var anchor = State().Materials.Anchors.FirstOrDefault(a => a.Id == targetId);
            if (anchor?.Depth.Canonical is { } d)
            {
                var seg = State().Segmentation.Segments
                    .FirstOrDefault(s => d >= s.TopM - 1e-9 && d <= s.BottomM + 1e-9);
                if (seg is not null) Recompute(new[] { seg.Id }, $"锚点 {targetId} 状态变更");
            }
        }
        return evt;
    }

    public EventRecord ReviseParams(ModelParams next, string reason)
    {
        var state = State();
        var evt = _store.Append(EventKinds.ParamRevised, JsonSerializer.Serialize(new
        { from = state.Params.Version, to = next.Version, @params = next, reason }, Projector.Json),
            new[] { $"seq:{state.HeadSeq}" }, next.Version, "param-revision");
        Recompute(null, $"参数修订 {state.Params.Version} → {next.Version}");
        return evt;
    }

    // ---- 导出 / 导入 ----

    public string Export() => _store.ExportJson();

    public ImportOutcome Import(string json)
    {
        try { _store.ImportJson(json); }
        catch (FormatCorruptException ex) { return new FormatCorrupt(ex.Message, ex.Position); }
        return new ImportOk(_store.HeadSeq, 0);
    }
}
