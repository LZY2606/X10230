using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chrono.Core;
using Chrono.Core.Dossier;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Model;
using Chrono.Core.Quantities;

namespace Chrono.Storage;

/// <summary>
/// Replays the Kettle Bay narrative offline: initial import & review, one parameter revision,
/// one local-only source amendment, and one conflicting human decision. The replay deliberately
/// passes through every named boundary situation and keeps both accepted and rejected outcomes.
/// </summary>
public sealed class CaseSeeder
{
    public sealed record SeedSummary(
        IReadOnlyList<string> Branches,
        IReadOnlyList<string> Situations,
        string? FrozenDerivationId);

    private readonly ProjectAppService _app;
    private readonly CaseImporter _importer = new();
    private const string Core = "HKB-2024-C";

    public CaseSeeder(ProjectAppService app) => _app = app;

    public SeedSummary SeedFromDirectory(string caseDir)
    {
        if (_app.Events.Count > 0)
            return new SeedSummary(_app.Events.Select(e => e.Branch).Distinct().ToList(),
                new List<string>(), _app.State().FrozenDerivationId);

        var parsed = _importer.ParseDirectory(caseDir);
        if (!parsed.Ok)
            throw new InvalidOperationException($"case package corrupt: {parsed.FormatError}");

        // stage 0 — project creation
        _app.AppendRaw(EventTypes.ProjectCreated, "main",
            new { projectId = "project/kettle-bay", coreId = Core, caseDir,
                  caseVersion = "2024-initial" }, actor: "sed-team");
        _app.SetRules("main", RuleSet.Default, "sed-team");

        foreach (var m in parsed.Materials) _app.ImportMaterial("main", m);
        var baseline = _app.Derive("main", "initial-import");

        var situations = new List<string> { "baseline_reviewed" };

        // stage 1 — parameter revision (rules/v2): full re-derivation, new parameter version recorded
        _app.SetRules("main", RuleSet.Revised, "sed-team");
        var afterRules = _app.Derive("main", "parameter-revision");
        situations.Add("parameter_revision_v2");

        // stage 2 — local-only source amendment: revised date near the base of unit I
        var revised = new RadiometricDateMaterial(
            "date/HKB-C-052-revised", "amendment/2024-r2", "sed-team-directive-r2", 1,
            Core, "Beta-482052", "330.0", "cm", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown,
            new NormalAgeDistribution(3710, 48, "BP1950"),
            "reservoir/marine-delta-r-kettle", new Depth(330));
        _app.AmendSource("main", revised, "date/HKB-C-052", "sed-team");
        var (afterLocal, unchanged) = _app.RecomputeScoped("main", afterRules.Derivation!);
        situations.Add($"local_recompute:unchanged[{string.Join(",", unchanged)}]");

        // stage 3 — conflicting human decision: adopt one of the tied event correlations
        _app.ChooseCorrelation("main", "tephra-at-265", "event/tephra-KBT1", "reviewer-A");
        var decided = _app.Derive("main", "conflicting-decision");
        situations.Add("conflicting_human_decision_recorded");

        // review path: adopt one candidate, reject another, then freeze
        var mainCandidate = decided.Derivation!.Tracks
            .SelectMany(t => t.Candidates).FirstOrDefault(c => c.Feasible);
        if (mainCandidate is not null)
        {
            _app.AppendRaw(EventTypes.CandidateAdopted, "main",
                new { candidateId = mainCandidate.Id, trackId = mainCandidate.TrackId }, "reviewer-A",
                ruleVersion: RuleSet.Revised.Version);
            var alt = decided.Derivation!.Tracks.SelectMany(t => t.Candidates)
                .FirstOrDefault(c => c.Feasible && c.Id != mainCandidate.Id);
            if (alt is not null) _app.RejectCandidate("main", alt.Id, "reviewer-B");
        }
        var frozen = _app.Freeze("main", decided.Derivation!.DerivationId,
            mainCandidate?.Id, "reviewer-A");
        situations.Add("concurrent_freeze:first_wins");

        // ---- dossier branches: each changes exactly one direct source from the baseline ----
        SeedDuplicateDepth(parsed.Materials);
        SeedUnconformity(parsed.Materials);
        SeedInversion(parsed.Materials);
        SeedTiedAnchors(parsed.Materials);
        SeedLongTail(parsed.Materials);
        SeedConcurrentFreeze(parsed.Materials, afterLocal);
        situations.AddRange(new[]
        {
            "duplicate_depth", "unconformity", "inverted_interval",
            "tied_anchors", "long_tail_distribution", "concurrent_freeze_second_refused"
        });

        var branches = _app.Events.Select(e => e.Branch).Distinct().OrderBy(b => b).ToList();
        return new SeedSummary(branches, situations,
            frozen.Event is null ? null : mainCandidate?.Id);
    }

    private void SeedBaselineBranch(string branch, IReadOnlyList<Material> materials, RuleSet rules)
    {
        _app.AppendRaw(EventTypes.ProjectCreated, branch,
            new { projectId = $"project/{branch}", coreId = Core, forkOf = "main" }, "dossier");
        _app.SetRules(branch, rules, "dossier");
        foreach (var m in materials) _app.ImportMaterial(branch, m);
    }

    private void SeedDuplicateDepth(IReadOnlyList<Material> baseMaterials)
    {
        const string b = "dup-depth";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Default);
        var dup = new RadiometricDateMaterial(
            "date/dup-at-188", "dossier/dup-depth", "counter-example", 1,
            Core, "Beta-490001", "1.88", "m", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, new NormalAgeDistribution(2760, 40, "BP1950"),
            "reservoir/marine-delta-r-kettle", new Depth(188));
        _app.ImportMaterial(b, dup, "dossier");
        _app.Derive(b, "dossier");
    }

    private void SeedUnconformity(IReadOnlyList<Material> baseMaterials)
    {
        const string b = "unconformity";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Default);
        var gap = new GapMaterial("gap/new-300", "dossier/unconformity", "counter-example", 1,
            Core, GapKind.Unconformity, "3.00", "m", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, "inserted angular unconformity", new Depth(300));
        _app.ImportMaterial(b, gap, "dossier");
        _app.Derive(b, "dossier");
    }

    private void SeedInversion(IReadOnlyList<Material> baseMaterials)
    {
        const string b = "inversion";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Default);
        // make the upper anchor older than the anchor below it within continuous unit I
        var inverted = new RadiometricDateMaterial(
            "date/HKB-C-014-inverted", "dossier/inversion", "counter-example", 1,
            Core, "Beta-482014", "62.5", "cm", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, new NormalAgeDistribution(2980, 30, "BP1950"),
            "reservoir/marine-delta-r-kettle", new Depth(62.5));
        _app.AmendSource(b, inverted, "date/HKB-C-014", "dossier");
        _app.Derive(b, "dossier");
    }

    private void SeedTiedAnchors(IReadOnlyList<Material> baseMaterials)
    {
        const string b = "tied-anchors";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Default);
        // unresolved on this branch: both members of the group active -> both candidates retained
        _app.AppendRaw(EventTypes.CorrelationChosen, b,
            new { correlationGroup = "__none__", chosenEventTieId = "__none__" }, "dossier");
        var a = new EventTieMaterial("event/tie-A", "dossier/tied", "counter-example", 1,
            Core, "TIE-X", "265.0", "cm", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, new NormalAgeDistribution(3050, 40, "BP1950"),
            "tie option A", new Depth(265)) { CorrelationGroup = "tie-x-group" };
        var c2 = new EventTieMaterial("event/tie-B", "dossier/tied", "counter-example", 2,
            Core, "TIE-X", "265.0", "cm", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, new NormalAgeDistribution(3050, 40, "BP1950"),
            "tie option B", new Depth(265)) { CorrelationGroup = "tie-x-group" };
        _app.ImportMaterial(b, a, "dossier");
        _app.ImportMaterial(b, c2, "dossier");
        _app.Derive(b, "dossier");
    }

    private void SeedLongTail(IReadOnlyList<Material> baseMaterials)
    {
        const string b = "long-tail";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Default);
        var tail = new MixtureAgeDistribution(new List<MixtureComponent>
        {
            new(0.85, new NormalAgeDistribution(6100, 50, "BP1950")),
            new(0.12, new NormalAgeDistribution(7000, 80, "BP1950")),
            new(0.03, new StudentTAgeDistribution(8600, 200, 3, "BP1950"))
        }, "BP1950");
        var m = new RadiometricDateMaterial("date/HKB-C-073-tail", "dossier/long-tail", "counter-example", 1,
            Core, "Beta-482073", "510.0", "cm", "cm b.s.f. (sea floor)",
            DepthDirection.PositiveDown, tail, "reservoir/marine-delta-r-kettle", new Depth(510));
        _app.AmendSource(b, m, "date/HKB-C-073", "dossier");
        _app.Derive(b, "dossier");
    }

    private void SeedConcurrentFreeze(IReadOnlyList<Material> baseMaterials, DerivationResult afterLocal)
    {
        const string b = "concurrent-freeze";
        SeedBaselineBranch(b, baseMaterials, RuleSet.Revised);
        var derived = _app.Derive(b, "dossier");
        var cand = derived.Derivation!.Tracks.SelectMany(t => t.Candidates)
            .FirstOrDefault(c => c.Feasible);
        var first = _app.Freeze(b, derived.Derivation!.DerivationId, cand?.Id, "reviewer-A");
        var second = _app.Freeze(b, derived.Derivation!.DerivationId, cand?.Id, "reviewer-B");
        if (first.Ok && !second.Ok && second.Event is not null)
        { /* both outcomes retained: freeze + refused-freeze-as-rejection */ }
    }
}
