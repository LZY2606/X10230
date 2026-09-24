using System.Text.Json;

namespace AgeModel;

/// <summary>应用服务：业务规则在服务端，存储与引擎都可被测试直接调用。</summary>
public sealed class Service
{
    public Store Store { get; }
    private ParamSet _params = ParamSet.V1;

    public Service(Store store)
    {
        Store = store;
        _params = _params with { Version = store.GetParamVersion() };
    }

    public ParamSet Params => _params;

    /// <summary>导入材料：保留原始表示，派生层才规范化；不完整输入返回带位置诊断。</summary>
    public Outcome<List<string>> ImportMaterials(List<Material> materials, string source)
    {
        var diags = new List<Diagnostic>();
        var ids = new List<string>();
        var evt = Store.AppendEvent("import", new { source, count = materials.Count });
        foreach (var m in materials)
        {
            if (string.IsNullOrWhiteSpace(m.Id))
            {
                diags.Add(new("missing-id", $"material of kind {m.Kind} has no id; position {ids.Count}", null, null, "error"));
                continue;
            }
            if (Store.MaterialExists(m.Id, m.Version)) continue; // 幂等：等价输入稳定
            Store.AddMaterial(m, evt.Seq);
            Store.AppendEvent("material-added", new { m.Id, m.Version, m.Kind, raw = m.Raw }, new() { evt.Id });
            ids.Add(m.Id);
        }
        var re = RederiveLocal(materials.Select(m => m.Id).ToHashSet(), "import");
        diags.AddRange(re.Diagnostics);
        return new Outcome<List<string>>
        {
            Status = diags.Any(d => d.Severity == "error") ? OutcomeStatus.Diagnostics : OutcomeStatus.Ok,
            Value = ids, Diagnostics = diags
        };
    }

    /// <summary>局部重算：只有依赖集与变更材料相交的段才重新派生，其余保持原记录。</summary>
    public Outcome<List<ModelCandidate>> RederiveLocal(HashSet<string> changedIds, string reason)
    {
        var seq = Store.NextSeqPeek() + 1;
        var materials = Store.MaterialsAsOf(long.MaxValue);
        var cores = materials.OfType<DepthDatum>().Select(d => d.CoreId)
            .Concat(materials.OfType<RadioAge>().Select(a => a.CoreId)).Distinct().ToList();
        var allDiags = new List<Diagnostic>();
        var saved = new List<ModelCandidate>();
        foreach (var core in cores)
        {
            var outcome = Engine.Derive(core, materials, _params, seq);
            allDiags.AddRange(outcome.Diagnostics);
            if (outcome.Status is OutcomeStatus.BudgetExhausted or OutcomeStatus.FormatCorrupt)
                return new Outcome<List<ModelCandidate>> { Status = outcome.Status, Diagnostics = allDiags };
            var fresh = outcome.Value!.Candidates;
            var existing = Store.ModelsDerivedAsOf(long.MaxValue).Where(m => m.CoreId == core).ToList();
            foreach (var cand in fresh)
            {
                var affected = cand.DependsOn.Any(changedIds.Contains);
                var priorExists = existing.Any(e => e.SegmentIndex == cand.SegmentIndex && e.CandidateIndex == cand.CandidateIndex);
                if (!affected && priorExists) continue; // 未受影响：保留旧派生（局部重算）
                Store.SaveModel(cand);
                saved.Add(cand);
            }
            if (saved.Count > 0)
                Store.AppendEvent("models-derived",
                    new { coreId = core, reason, modelIds = saved.Select(m => m.Id).ToList(), inputHash = Hashing.OfJson(saved.Select(m => m.InputHash)) },
                    saved.SelectMany(m => m.DependsOn).Distinct().ToList(), _params.Version);
        }
        return new Outcome<List<ModelCandidate>> { Status = allDiags.Any(d => d.Severity == "error") ? OutcomeStatus.Diagnostics : OutcomeStatus.Ok, Value = saved, Diagnostics = allDiags };
    }

    /// <summary>人工决定（接受/否定/参数调整）生成事件；冲突决定并存保留。</summary>
    public AppEvent Decide(string targetId, bool accept, string reason, string reviewer)
    {
        var evt = Store.AppendEvent("decision", new { targetId, accept, reason, reviewer }, new() { targetId });
        if (!accept)
        {
            var seq = evt.Seq;
            Store.AddMaterial(new ExclusionRecord
            {
                Id = $"excl-{targetId}-{reviewer}", Version = 1, Raw = reason,
                TargetId = targetId, Reason = reason, DecidedBy = reviewer
            }, seq);
            RederiveLocal(new() { targetId }, $"exclusion:{targetId}");
        }
        return evt;
    }

    public AppEvent AdjustParams(double monotonicToleranceKa, int maxCandidates, int budget, string by)
    {
        _params = new ParamSet($"p{Hashing.Of($"{monotonicToleranceKa}:{maxCandidates}:{budget}:{DateTimeOffset.UtcNow.Ticks}")[..6]}",
            monotonicToleranceKa, maxCandidates, budget);
        Store.SetParamVersion(_params.Version);
        var evt = Store.AppendEvent("param-adjust", new { version = _params.Version, monotonicToleranceKa, maxCandidates, budget, by });
        var all = Store.MaterialsAsOf(long.MaxValue).Select(m => m.Id).ToHashSet();
        RederiveLocal(all, "param-adjust");
        return evt;
    }

    /// <summary>并发冻结：第二个冻结者得到 Conflict。</summary>
    public Outcome<string> Freeze(string modelId, string by)
    {
        var r = Store.Freeze(modelId, by);
        if (r.Status == OutcomeStatus.Ok)
            Store.AppendEvent("model-frozen", new { modelId, by }, new() { modelId });
        return r;
    }

    /// <summary>新锚点影响对比：与已冻结模型比较，不落库为正式材料。</summary>
    public Outcome<object> ProposeAnchor(RadioAge proposed)
    {
        var materials = Store.MaterialsAsOf(long.MaxValue).Concat(new[] { proposed }).ToList();
        var outcome = Engine.Derive(proposed.CoreId, materials, _params, Store.NextSeqPeek() + 1);
        if (outcome.Value is null)
            return new Outcome<object> { Status = outcome.Status, Diagnostics = outcome.Diagnostics };
        var frozen = Store.ModelsDerivedAsOf(long.MaxValue)
            .Where(m => m.CoreId == proposed.CoreId && m.Status == "frozen").ToList();
        var comparison = outcome.Value.Candidates.Select(c =>
        {
            var baseline = frozen.FirstOrDefault(f => f.SegmentIndex == c.SegmentIndex && f.CandidateIndex == c.CandidateIndex);
            object? delta = baseline is null ? null : new
            {
                baselineId = baseline.Id,
                rateMinDelta = c.RateMinCmPerKa - baseline.RateMinCmPerKa,
                rateMaxDelta = c.RateMaxCmPerKa - baseline.RateMaxCmPerKa,
                bandMedianDeltaAtAnchor = BandAt(c, proposed.DepthM) - BandAt(baseline, proposed.DepthM)
            };
            return new { candidate = c, impact = delta };
        }).ToList();
        Store.AppendEvent("anchor-proposed", new { proposedId = proposed.Id, depth = proposed.DepthM, comparedAgainst = frozen.Select(f => f.Id).ToList() });
        return Outcome<object>.Ok(new { diagnostics = outcome.Diagnostics, comparison });
    }

    private static double BandAt(ModelCandidate m, double depth)
    {
        var b = m.UncertaintyBand.OrderBy(x => Math.Abs(x.DepthM - depth)).First();
        return b.P50Ka;
    }

    /// <summary>审阅状态：asOf 支持时间回溯——恢复当时的依赖版本与可见结论。</summary>
    public object StateAsOf(long? asOfSeq)
    {
        var seq = asOfSeq ?? long.MaxValue;
        var materials = Store.MaterialsAsOf(seq);
        var events = Store.EventsAsOf(seq);
        var models = Store.ModelsDerivedAsOf(seq);
        var cores = materials.OfType<DepthDatum>().Select(d => d.CoreId)
            .Concat(materials.OfType<RadioAge>().Select(a => a.CoreId)).Distinct().OrderBy(x => x).ToList();
        var segs = cores.SelectMany(core =>
        {
            var o = Engine.Derive(core, materials, _params with { Version = models.FirstOrDefault()?.ParamVersion ?? _params.Version }, seq);
            return o.Value?.Segments.Select(s => new { coreId = core, segment = s }) ?? Enumerable.Empty<object>();
        }).ToList();
        return new
        {
            asOfSeq = seq == long.MaxValue ? Store.NextSeqPeek() : seq,
            paramVersion = Store.GetParamVersion(),
            materials = materials.Select(m => new
            {
                m.Id, m.Version, kind = m.Kind, m.Raw, m.Unit, m.Datum, data = m
            }),
            cores,
            segments = segs,
            models,
            events = events.Select(e => new { e.Seq, e.Id, e.Kind, payload = e.Payload, e.ParamVersion, e.Parents, e.CreatedAt }),
            latestSeq = Store.NextSeqPeek()
        };
    }
}
