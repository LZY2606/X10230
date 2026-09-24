using System.Security.Cryptography;
using System.Text;
using AgeDepth.Domain;

namespace AgeDepth.Engine;

public sealed record EngineOptions(int MaxCandidates = 32);

public sealed record RecomputeReport(
    ModelSet ModelSet,
    IReadOnlyList<string> AffectedSegments,
    IReadOnlyList<string> ReusedSegments);

/// <summary>
/// Derives age-depth candidate models from immutable materials.
/// Rules enforced here (and emitted as machine-readable judgments):
///  - ages may only be monotonic inside an unbroken segment;
///  - depth gaps and unconformities are explicit boundaries;
///  - an anchor's distribution is never replaced by its mean;
///  - multiple equally defensible models are all retained.
/// </summary>
public static class AgeDepthEngine
{
    private sealed record PreparedAnchor(AgeAnchor Anchor, AgeDistribution Age, double DepthM);

    private sealed record SegmentResult(
        SegmentModel Segment,
        IReadOnlyList<CandidateModel> Candidates,
        string InputDigest);

    public static ComputationResult<ModelSet> Derive(
        CoreSection section, ParameterSet parameters, EngineOptions? options = null)
    {
        var report = DeriveInternal(section, parameters, null, options ?? new EngineOptions());
        return report switch
        {
            ComputationResult<RecomputeReport>.Ok ok => new ComputationResult<ModelSet>.Ok(ok.Value.ModelSet),
            ComputationResult<RecomputeReport>.BudgetExhausted b => new ComputationResult<ModelSet>.BudgetExhausted(b.Scope, b.Used, b.Limit),
            ComputationResult<RecomputeReport>.Unsolvable u => new ComputationResult<ModelSet>.Unsolvable(u.Reason, u.Diagnostics),
            _ => new ComputationResult<ModelSet>.Unsolvable("unexpected", Array.Empty<Diagnostic>()),
        };
    }

    /// <summary>
    /// Local recompute: only segments whose input digest changed are re-derived;
    /// untouched segments keep their previous candidate identities.
    /// </summary>
    public static ComputationResult<RecomputeReport> DeriveIncremental(
        ModelSet? previous, CoreSection section, ParameterSet parameters, EngineOptions? options = null)
        => DeriveInternal(section, parameters, previous, options ?? new EngineOptions());

    private static ComputationResult<RecomputeReport> DeriveInternal(
        CoreSection section, ParameterSet parameters, ModelSet? previous, EngineOptions options)
    {
        var diagnostics = new List<Diagnostic>();
        var judgments = new List<Judgment>();

        // ---- 1. normalize depths; unconvertible values stay in place with diagnostics
        var usable = new List<PreparedAnchor>();
        foreach (var anchor in section.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var pos = $"anchor:{anchor.Id}";
            if (anchor.Depth.Meters is null)
            {
                diagnostics.Add(new Diagnostic("depth-unconvertible", pos,
                    anchor.Depth.Diagnostic ?? "unconvertible depth", anchor.Id));
                continue;
            }
            var age = anchor.Age;
            if (anchor.Age.Unit.Trim.Length > 0 && anchor.ReservoirOffsetRef is { } offsetRef)
            {
                if (parameters.ReservoirCorrections.TryGetValue(offsetRef, out var correction))
                    age = age.Shift(correction);
                else if (section: 0 == 1) { }
            }
            usable.Add(new PreparedAnchor(anchor, age, anchor.Dep. Meters ?? 0));
        }
        return null!;
    }
}
