using System.Text;
using System.Text.Json;

namespace ChronoAge.Core;

public sealed record DerivationRun(
    OutcomeKind Outcome,
    DerivationArtifact? Artifact,
    IReadOnlyList<DepoSegment> Segments,
    IReadOnlyList<DerivedAnchor> Anchors,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    string InputHash,
    IReadOnlyList<string> DependsOn);

/// <summary>
/// Server-side orchestration of a derivation run. Computes an input summary over the exact
/// active material versions + parameter version, tracks dependencies and supports local
/// recompute: a source change only invalidates derivations whose dependency set intersects it.
/// </summary>
public sealed class DerivationService
{
    private readonly Solver _solver = new();

    public static string ComputeInputHash(ProjectState st)
    {
        var sb = new StringBuilder();
        sb.Append("rule=").Append(st.Parameters.Version).Append('\n');
        sb.Append("eps=").Append(st.Parameters.DepthEpsilonM.ToString("R")).Append('\n');
        sb.Append("n=").Append(st.Parameters.SamplesPerAnchor).Append('\n');
        sb.Append("seed=").Append(st.Parameters.RngSeed).Append('\n');
        foreach (var m in st.ActiveMaterials.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var json = JsonSerializer.Serialize<Material>(m, ProjectState.JsonOpts);
            sb.Append(m.Id).Append('=').Append(json).Append('\n');
        }
        sb.Append("exclusions=");
        sb.AppendJoin(",", st.Excluded.OrderBy(x => x, StringComparer.Ordinal));
        sb.Append('\n');
        foreach (var kv in st.OverlapCalls.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("ovl:").Append(kv.Key).Append('=').Append(kv.Value.Choice).Append('\n');
        foreach (var kv in st.BoundaryCalls.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("bnd:").Append(kv.Key).Append('=').Append(kv.Value.Interpretation).Append('\n');
        foreach (var kv in st.TieCalls.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("tie:").Append(kv.Key).Append('=').Append(kv.Value.Order).Append('\n');
        return StableIds.ContentHash(sb.ToString());
    }

    public DerivationRun Run(ProjectState st, DateTimeOffset? atUtc = null)
    {
        var when = atUtc ?? DateTimeOffset.UtcNow;
        var diags = new List<Diagnostic>();

        var datum = st.LatestOfType<DepthDatum>();
        if (datum is null)
        {
            diags.Add(Diag.Error("DER_NO_DATUM", "No depth datum is available; cannot derive a depth-age model.", null, "depth_datum"));
            return Fail(OutcomeKind.Incomplete, st, diags, when);
        }
        var sections = st.OrderedSections().ToList();
        if (sections.Count == 0)
        {
            diags.Add(Diag.Error("DER_NO_SECTIONS", "No core sections are available; cannot segment the core.", null, "sections"));
            return Fail(OutcomeKind.Incomplete, st, diags, when);
        }

        var segments = Engine.BuildSegments(st);
        var (anchors, anchorDiags) = Engine.BuildAnchors(st);
        diags.AddRange(anchorDiags);
        if (anchors.Count < 2)
        {
            diags.Add(Diag.Error("DER_TOO_FEW_ANCHORS",
                $"Only {anchors.Count} usable anchor(s); a depth-age model needs at least two.", null, "anchors"));
            return Fail(OutcomeKind.Incomplete, st, diags, when);
        }

        var enumOutcome = CandidateTree.Enumerate(st, segments, anchors);
        if (!enumOutcome.IsOk)
        {
            diags.AddRange(enumOutcome.Diagnostics);
            return new DerivationRun(enumOutcome.Kind, null, segments, anchors, Array.Empty<ModelCandidate>(),
                diags, ComputeInputHash(st), BuildDeps(st));
        }

        var inputHash = ComputeInputHash(st);
        var deps = BuildDeps(st);
        var artifact = _solver.Solve(st, segments, enumOutcome.Value!, anchors, inputHash, deps, when);
        diags.AddRange(artifact.Diagnostics);
        var hadBudgetError = artifact.Diagnostics.Any(d => d.Code == "SOLVE_BUDGET");

        return new DerivationRun(
            hadBudgetError ? OutcomeKind.BudgetExhausted : OutcomeKind.Ok,
            hadBudgetError ? null : artifact,
            segments, anchors, enumOutcome.Value!, diags, inputHash, deps);
    }

    /// <summary>
    /// Returns which stored derivations a material/decision/parameter change invalidates.
    /// Anything outside the touched dependency set stays byte-identical (local recompute).
    /// </summary>
    public static IReadOnlyList<string> AffectedDerivations(
        IEnumerable<DerivationArtifact> stored, IReadOnlyCollection<string> touchedDepIds)
    {
        return stored
            .Where(a => a.DependsOn.Any(d => touchedDepIds.Contains(d)))
            .Select(a => a.ArtifactId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> BuildDeps(ProjectState st)
    {
        var deps = new List<string> { $"rule:{st.Parameters.Version}" };
        deps.AddRange(st.ActiveMaterials.Select(m => $"mat:{m.Id}"));
        deps.AddRange(st.Decisions.Select(e => $"dec:{e.EventId}"));
        return deps.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static DerivationRun Fail(OutcomeKind kind, ProjectState st, List<Diagnostic> diags, DateTimeOffset when)
        => new(kind, null, Array.Empty<DepoSegment>(), Array.Empty<DerivedAnchor>(),
            Array.Empty<ModelCandidate>(), diags, ComputeInputHash(st), BuildDeps(st));
}
