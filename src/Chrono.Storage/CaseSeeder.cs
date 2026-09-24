using Chrono.Core.Case;
using Chrono.Core.Domain;
using Chrono.Core.Engine;
using Chrono.Core.Models;

namespace Chrono.Storage;

/// <summary>
/// Deterministic replay of the offline case pack narrative. A reviewer can trace
/// every final difference back to which material, which rule version and which
/// decision produced it. The replay genuinely passes through:
/// depth duplicate, unconformity, inverted section, tied anchors, long-tail
/// distribution, local recompute, and concurrent freeze.
/// </summary>
public sealed class CaseSeeder
{
    private readonly SessionService _session;
    public const string ProjectId = "GS-LOCH-01";

    public CaseSeeder(SessionService session) => _session = session;

    public async Task<SeedReport> RunAsync(string caseText, CancellationToken ct = default)
    {
        var baseline = CaseFormat.Parse(caseText);
        if (baseline.Kind != OutcomeKind.Ok && baseline.Kind != OutcomeKind.SemanticImpossible)
            return new SeedReport(false, baseline.Kind, baseline.Diagnostics);

        var p0 = new ParameterSet(ParameterSet.Current, 0.05, 1, 200, 120, 256,
            new Dictionary<string, string>
            {
                ["maxTieDepthToleranceM"] = "0.05", ["minRateCmPerKyr"] = "1",
                ["maxRateCmPerKyr"] = "200", ["reservoirDefaultSdYr"] = "120", ["candidateBudget"] = "256"
            });

        var batch = new ImportBatchPayload(baseline.Datum, baseline.Segments, baseline.Anchors,
            baseline.Reservoirs, baseline.EventLayers, baseline.Exclusions,
            baseline.Diagnostics, baseline.Judgments);
        var created = await _session.SeedFromImportAsync(batch, ProjectId,
            "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系", p0, ct);
        if (!created.Ok) return new SeedReport(false, created.Kind, created.Diagnostics);

        var steps = new List<string>();

        // Turn 0 — baseline derivation (duplicate/tie/long-tail/inversion all present).
        var d0 = await _session.DeriveAsync("main", ct: ct);
        steps.Add("baseline-derive:" + d0.Kind);

        // Turn 1 — parameter revision: tighten max deposition rate.
        var p1 = p0 with { Version = "rules-2026.2", MaxRateCmPerKyr = 120 };
        var r1 = await _session.ReviseParametersAsync(p1, "收紧最大沉积速率 200→120 cm/kyr", ct);
        steps.Add("parameter-revision:" + r1.Kind);

        // Turn 2 — local-only source change: re-measure ANC-03 (does not touch ANC-10).
        var s2 = _session.State();
        var a03 = s2.Anchors["ANC-03"] with
        {
            Distribution = new NormalDist(4180, 110, "yr BP", Array.Empty<Diagnostic>()),
            Revision = 2, Raw = a03_ReMeasured()
        };
        var r2 = await _session.ReviseSourceAsync("ANC-03", a03, new[] { "ANC-03" },
            "实验室重测 ANC-03：中心年龄 4100→4180 BP（仅局部重算）", ct);
        steps.Add("local-source-revision:" + r2.Kind);

        // Turn 3 — conflicting human decision: duplicate ANC-13. Two reviewers
        // disagree; BOTH legal branches are retained (not collapsed to one).
        await _session.CreateBranchAsync("branch-keep-dup", "保留重复送样取均值（审阅者甲）", false,
            "深度重复 ANC-13：保留并合并", ct);
        await _session.CreateBranchAsync("branch-reject-dup", "拒绝重复送样（审阅者乙）", false,
            "深度重复 ANC-13：拒绝", ct);
        await _session.ResolveConflictAsync("conflict-dup-ANC-13",
            "合并重复送样", "拒绝重复送样", "branch-reject-dup",
            "互斥人工决定：两套分支均保留，等待组长裁决", new[] { "ANC-13" }, ct);
        steps.Add("conflict-branches:ok");

        // Turn 4 — freeze the baseline main candidate.
        var stateNow = _session.State();
        var frozenTarget = stateNow.Candidates.Values
            .Where(c => c.Accepted && c.AnchorIds.Contains("ANC-03"))
            .OrderBy(c => c.Id).First();
        var fr = await _session.FreezeAsync(frozenTarget.Id, ct);
        steps.Add("freeze:" + fr.Kind + ":" + (fr.EventId ?? "rejected"));

        // Turn 5 — concurrent freeze test: trying to mutate main now must be
        // rejected with a structured FROZEN-CONFLICT result.
        var mut = await _session.ReviseParametersAsync(p1 with { MaxRateCmPerKyr = 999 },
            "冻结后试图篡改（应被拒绝）", ct);
        steps.Add("frozen-mutation-rejected:" + (!mut.Ok && mut.Kind == OutcomeKind.SemanticImpossible));

        // Turn 6 — new anchor impact on a scratch branch (frozen model untouched).
        await _session.CreateBranchAsync("scratch-new-anchor", "新锚点影响比较（临时分支）", true,
            "冻结后比较新锚点 ANC-99", ct);
        var newAnchor = new AgeAnchor("ANC-99", "ANC-99", Qty.Parse("2.10 m", "depth"),
            new NormalDist(2600, 130, "yr BP", Array.Empty<Diagnostic>()),
            "新送样炭屑", "AMS14C-日历校正", null, null,
            AnchorStatus.Active, null, Array.Empty<Diagnostic>(), "ANC-99|2.10 m|N(2600±130 BP)|新送样炭屑|AMS14C|-|-");
        var add = await _session.AddAnchorOnBranchAsync("scratch-new-anchor", newAnchor, ct);
        var dScratch = await _session.DeriveAsync("scratch-new-anchor", ct: ct);
        steps.Add("scratch-impact:" + add.Ok + ":" + dScratch.Kind);

        // Turn 7 — counterexample probes: one direct source changed per probe,
        // scope compared against the baseline; adopted & not-adopted retained.
        foreach (var probe in CounterexampleProbes.Build(_session.State(), frozenTarget.Id))
            await _session.RecordProbeAsync(probe, ct);
        steps.Add("counterexample-probes:" + CounterexampleProbes.Build(_session.State(), frozenTarget.Id).Count);

        return new SeedReport(true, OutcomeKind.Ok, baseline.Diagnostics, steps);
    }

    private static string a03_ReMeasured() =>
        "ANC-03|3.20 m|N(4180±110 BP)|陆源树枝|AMS14C-重测|-|-";
}

public sealed record SeedReport(bool Ok, OutcomeKind Kind, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Steps = null);
