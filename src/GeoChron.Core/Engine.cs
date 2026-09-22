using System.Text.Json;

namespace GeoChron.Core;

/// <summary>
/// 深度-年龄建模引擎：输入案例包 + 参数版本 → 结构化结果。
/// 规则：年龄只能在不断层段内单调；缺口/不整合是显式边界；
/// 分布保留为分布；多个等优模型全部保留。
/// </summary>
public sealed class AgeModelEngine
{
    public const int DefaultDrawBudget = 2_000_000;
    public const int DefaultCandidateBudget = 64;

    public static (CasePack? pack, IReadOnlyList<Diagnostic> diags) ParsePack(string json)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<CasePack>(json, CaseJson.Options);
            if (pack is null)
                return (null, new[] { new Diagnostic("P001", "$", "案例包反序列化为空") });
            var diags = new List<Diagnostic>();
            if (pack.Schema is not ("geochron-case-v1"))
                diags.Add(new Diagnostic("P002", "schema", $"不支持的案例包 schema '{pack.Schema}'，期望 geochron-case-v1"));
            return (pack, diags);
        }
        catch (JsonException ex)
        {
            return (null, new[] { new Diagnostic("P900", "$", $"格式损坏：{ex.Message}") });
        }
    }

    public EngineOutput Run(CasePack pack, ModelParams? parameterOverride = null,
        int? drawBudget = null, int? candidateBudget = null)
    {
        var p = parameterOverride ?? pack.Params;
        var inputSummary = ComputeInputSummary(pack, p);
        var allDiags = new List<Diagnostic>();
        var allFindings = new List<Finding>();

        var built = SectionBuilder.Build(pack);
        allDiags.AddRange(built.Diagnostics);
        allFindings.AddRange(built.Findings);
        var prepared = ConstraintPreparer.Prepare(pack, built.Sections);
        allDiags.AddRange(prepared.Diagnostics);
        allFindings.AddRange(prepared.Findings);

        var ledger = new ComputeBudgetLedger(drawBudget ?? DefaultDrawBudget, candidateBudget ?? DefaultCandidateBudget);
        var fitter = new Fitter(ledger);
        var fitted = new List<FittedCandidate>();

        var activeIds = new HashSet<string>(prepared.ActiveIds);
        var specs = EnumerateSpecs(pack, built.Sections, prepared, activeIds, allFindings);

        foreach (var spec in specs)
        {
            if (!ledger.TryEnumerateCandidate())
            {
                return Finish(RunOutcome.BudgetExhausted, pack, p, inputSummary, allDiags, allFindings,
                    built.Sections, fitted, ledger, "候选枚举数超过预算（候选爆炸），与格式损坏/语义无解严格区分");
            }
            try
            {
                var fc = fitter.Fit(pack, p, built.Sections, prepared, spec, inputSummary);
                fitted.Add(fc);
                allFindings.AddRange(fc.Findings);
                allDiags.AddRange(fc.Diagnostics);
            }
            catch (BudgetExhaustedException)
            {
                ledger.RollbackCandidate();
                return Finish(RunOutcome.BudgetExhausted, pack, p, inputSummary, allDiags, allFindings,
                    built.Sections, fitted, ledger, "蒙特卡洛抽样预算耗尽，无法给出分位带（非语义无解）");
            }
        }

        var ranked = Rank(fitted);
        if (ranked.Count == 0 || ranked.All(r => !r.Legal))
        {
            allDiags.Add(new Diagnostic("E001", "candidates",
                "没有任何合法候选：材料无法唯一支撑深度-年龄模型（语义无解，区别于预算耗尽）"));
            return Finish(RunOutcome.SemanticUnresolved, pack, p, inputSummary, allDiags, allFindings,
                built.Sections, ranked, ledger, null);
        }
        return Finish(RunOutcome.Ok, pack, p, inputSummary, allDiags, allFindings, built.Sections, ranked, ledger, null);
    }

    private static List<FitSpec> EnumerateSpecs(CasePack pack, IReadOnlyList<Section> sections,
        PreparedConstraints prepared, HashSet<string> activeIds, List<Finding> findings)
    {
        var specs = new List<FitSpec>();
        var usable = prepared.All.Where(c => sections.SectionAt(c.DepthCm) is not null).ToList();
        var allActive = new HashSet<string>(usable.Where(c => activeIds.Contains(c.Id)).Select(c => c.Id));
        var none = new HashSet<string>();

        // 1) 严格分段线性：不跨缺口，不插入边界。
        specs.Add(new FitSpec(FitStrategyKind.StrictLinear,
            new Dictionary<string, string> { ["description"] = "分段严格线性、严格单调（不引入不整合）" },
            allActive, none));
        // 2) 保序（isotonic）池化：允许等龄并列，不删除材料。
        specs.Add(new FitSpec(FitStrategyKind.Isotonic,
            new Dictionary<string, string> { ["description"] = "PAVA 保序回归，倒置合并为等龄块（保留全部锚点）" },
            allActive, none));

        // 3) 插入不整合：检测到倒置区间时，在倒置深度插入一个新的显式边界并切分层段。
        var inverted = DetectInversionDepths(usable.Where(c => allActive.Contains(c.Id)).ToList(), sections);
        foreach (var (secId, depth, pair) in inverted.Take(8))
        {
            var baseSec = sections.First(s => s.Id == secId);
            var gapId = $"gap-auto-{StableIds.Hash($"{secId}:{depth:0.######}")}";
            var split = SplitSection(baseSec, depth, gapId);
            var detail = new Dictionary<string, string>
            {
                ["description"] = $"在 {depth:0.##} cm 插入工作不整合边界（{pair}），分段重算",
                ["insertedAtDepthCm"] = depth.ToString("0.######"),
                ["pair"] = pair
            };
            specs.Add(new FitSpec(FitStrategyKind.InsertUnconformity, detail, allActive,
                new HashSet<string> { gapId }, split));
        }

        // 4) 排除倒置锚点：一次排除一个可排除（放射性）锚点；事件层不可排除。
        foreach (var c in usable.Where(c => allActive.Contains(c.Id) && c.Excludable && c.Kind == "anchor"))
        {
            var used = new HashSet<string>(allActive) { };
            // 仅在该锚点确实参与倒置/重复时才生成“排除它”候选，避免无意义候选爆炸。
            if (!involvedInConflict(c.Id, inverted, usable, sections)) continue;
            used.Remove(c.Id);
            specs.Add(new FitSpec(FitStrategyKind.ExcludeInversion,
                new Dictionary<string, string> { ["description"] = $"排除倒置锚点 {c.Id} 后保序拟合", ["excludedAnchor"] = c.Id },
                used, none));
        }
        return specs;
    }

    private static bool involvedInConflict(string id, List<(string secId, double depth, string pair)> inversions,
        List<Constraint> usable, IReadOnlyList<Section> sections)
    {
        if (inversions.Any(iv => iv.pair.Contains(id))) return true;
        // 深度重复并列也算冲突情境。
        var c = usable.FirstOrDefault(x => x.Id == id);
        return c is not null && usable.Any(o => o.Id != id && Math.Abs(o.DepthCm - c.DepthCm) < 1e-9);
    }

    private static List<(string secId, double depth, string pair)> DetectInversionDepths(
        List<Constraint> cons, IReadOnlyList<Section> sections)
    {
        var result = new List<(string, double, string)>();
        foreach (var sec in sections)
        {
            var inSec = cons.Where(c => c.DepthCm >= sec.TopCm - 1e-9 && c.DepthCm <= sec.BottomCm + 1e-9)
                .OrderBy(c => c.DepthCm).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            for (var i = 1; i < inSec.Count; i++)
            {
                if (inSec[i].DepthCm - inSec[i - 1].DepthCm <= 1e-9) continue;
                // 用中位数判断顺序倒置（不消费 MC 预算）。
                var a = inSec[i - 1].Distribution.Quantile(.5, 400, 0xA17UL ^ (ulong)i);
                var b = inSec[i].Distribution.Quantile(.5, 400, 0xB17UL ^ (ulong)i);
                if (b < a - 1e-9)
                    result.Add((sec.Id, (inSec[i - 1].DepthCm + inSec[i].DepthCm) / 2,
                        $"{inSec[i - 1].Id}|{inSec[i].Id}"));
            }
        }
        return result;
    }

    private static IReadOnlyList<Section> SplitSection(Section sec, double atDepth, string newGapId)
    {
        var upper = new Section($"{sec.Id}a", sec.Index, sec.TopCm, atDepth, sec.SegmentIds, null);
        var lower = new Section($"{sec.Id}b", sec.Index + 1, atDepth, sec.BottomCm, sec.SegmentIds, newGapId);
        return new[] { upper, lower };
    }

    private static List<FittedCandidate> Rank(List<FittedCandidate> fitted)
    {
        var ordered = fitted.OrderBy(f => f.Model.Score)
            .ThenBy(f => f.Model.Strategy, StringComparer.Ordinal)
            .ThenBy(f => string.Join(",", f.Model.UsedAnchorIds), StringComparer.Ordinal)
            .ThenBy(f => f.Model.Id, StringComparer.Ordinal)
            .ToList();
        var legalScores = ordered.Where(f => f.Legal).Select(f => f.Model.Score).ToList();
        var best = legalScores.Count == 0 ? double.NaN : legalScores.Min();
        var rank = 0;
        return ordered.Select(f =>
        {
            rank++;
            var equal = f.Legal && Math.Abs(f.Model.Score - best) < 1e-9;
            return f with { Model = f.Model with { Rank = rank, EqualOptimal = equal } };
        }).ToList();
    }

    private static EngineOutput Finish(RunOutcome outcome, CasePack pack, ModelParams p, string inputSummary,
        List<Diagnostic> diags, List<Finding> findings, IReadOnlyList<Section> sections,
        List<FittedCandidate> fitted, ComputeBudgetLedger ledger, string? budgetReason)
    {
        // 稳定排序：合法优先、分数、再稳定标识。
        var models = fitted.OrderBy(f => f.Legal ? 0 : 1).ThenBy(f => f.Model.Rank)
            .Select(f => f.Model).ToList();
        var budget = new ComputeBudget(
            p.McBudget, (int)Math.Min(int.MaxValue, ledger.McUsed / Math.Max(1, p.McBudget)),
            DefaultCandidateBudget, ledger.CandidatesEnumerated,
            outcome == RunOutcome.BudgetExhausted, budgetReason);
        return new EngineOutput(outcome, inputSummary, p.Version,
            diags.OrderBy(d => d.Path, StringComparer.Ordinal).ToList(),
            findings, sections, models, p.McBudget, models.Count, budget);
    }

    public static string ComputeInputSummary(CasePack pack, ModelParams p)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            pack.Schema, pack.CaseId, pack.DepthDatum,
            segments = pack.Segments.OrderBy(s => s.Id),
            gaps = pack.Gaps.OrderBy(g => g.Id),
            anchors = pack.Anchors.OrderBy(a => a.Id),
            reservoirOffsets = pack.ReservoirOffsets.OrderBy(r => r.Id),
            eventLayers = pack.EventLayers.OrderBy(e => e.Id),
            exclusions = pack.Exclusions.OrderBy(e => e.Id),
            p
        }, CaseJson.Options);
        return "sha256:" + StableIds.Hash(canonical);
    }
}
