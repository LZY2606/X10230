using System.Diagnostics;
using Chrono.Core.Materials;
using Chrono.Core.Units;

namespace Chrono.Core.Engine;

public sealed record BuildInputs(
    MaterialProject Project,
    ChronoParameters Parameters,
    BuildBudget Budget);

public sealed record RateInterval(double LowerCmPerKa, double UpperCmPerKa);

public sealed record ModelCandidate(
    string CandidateId,
    string ChainId,
    string Family,
    int Rank,
    bool Tied,
    double Score,
    double FitMaMeters,
    double CoveragePct,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<UncertaintyBandPoint> Band,
    IReadOnlyList<string> AnchorIds);

public sealed record UncertaintyBandPoint(
    double DepthMeters,
    double Q025CalBp,
    double Q500CalBp,
    double Q975CalBp,
    bool LongTailDominant);

public sealed record EngineBuildResult(
    BuildStatus Status,
    BuildReport Report,
    IReadOnlyList<ChronologyChain> Chains,
    IReadOnlyList<ModelCandidate> Candidates,
    InputFingerprint Fingerprint,
    int SamplesUsed,
    bool BudgetExceeded)
{
    public IEnumerable<ModelCandidate> CandidatesFor(string chainId) => Candidates.Where(c => c.ChainId == chainId);
}

public sealed record InputFingerprint(
    string Hash,
    string ParametersVersion,
    IReadOnlyList<string> MaterialHashParts);

public interface IChronoEngine
{
    EngineBuildResult Build(BuildInputs inputs);
}

public sealed class ChronoEngineService : IChronoEngine
{
    private readonly SegmentAnalyzer _analyzer = new();
    private readonly CandidateFitter _fitter = new();

    public EngineBuildResult Build(BuildInputs inputs)
    {
        var sw = Stopwatch.StartNew();
        var project = inputs.Project;

        var corrupt = project.RawTokens
            .Where(t => t.Status == RawParseStatus.Malformed)
            .Select(t => new BuildDiagnostic("FORMAT-CORRUPT", DiagnosticSeverity.Error,
                t.SourceId, t.Field, null, t.Diagnostic ?? "malformed value", t.RawText))
            .ToList();
        if (corrupt.Count > 0)
        {
            return new EngineBuildResult(BuildStatus.FormatCorrupt,
                new BuildReport(BuildStatus.FormatCorrupt, "source format is corrupt; no derivation performed", corrupt),
                Array.Empty<ChronologyChain>(), Array.Empty<ModelCandidate>(),
                Fingerprint(inputs), 0, false);
        }

        var analysis = _analyzer.Analyze(project);
        var semanticErrors = analysis.Chains
            .SelectMany(c => c.ViolationCodes
                .Where(v => v.StartsWith("SEMANTIC"))
                .Select(v => new BuildDiagnostic(v, DiagnosticSeverity.Error,
                    c.SegmentId, "chain", null, Describe(v), null)))
            .ToList();
        if (semanticErrors.Count > 0)
        {
            return new EngineBuildResult(BuildStatus.SemanticallyUnsolvable,
                new BuildReport(BuildStatus.SemanticallyUnsolvable,
                    "age order is inverted inside an unbroken segment; no legal monotone model exists", semanticErrors),
                analysis.Chains, Array.Empty<ModelCandidate>(), Fingerprint(inputs), 0, false);
        }

        var fit = _fitter.Fit(analysis.Chains, inputs.Parameters, inputs.Budget, sw);
        if (fit.BudgetExceeded)
        {
            var diag = new BuildDiagnostic("BUDGET-EXHAUSTED", DiagnosticSeverity.Error,
                "engine", "budget", null,
                $"candidate/sample budget exhausted (candidates>{inputs.Budget.MaxCandidates} or samples>{inputs.Budget.MaxSamples})", null);
            return new EngineBuildResult(BuildStatus.BudgetExhausted,
                new BuildReport(BuildStatus.BudgetExhausted,
                    "resource budget exhausted before all legal candidates were evaluated",
                    new[] { diag }), analysis.Chains, fit.Candidates, Fingerprint(inputs),
                fit.SamplesUsed, true);
        }

        var warnings = project.RawTokens
            .Where(t => t.Status == RawParseStatus.Unconverted)
            .Select(t => new BuildDiagnostic("UNCONVERTED-VALUE", DiagnosticSeverity.Warning,
                t.SourceId, t.Field, null,
                t.Diagnostic ?? "value left at source position", t.RawText))
            .ToList();

        return new EngineBuildResult(BuildStatus.Success,
            new BuildReport(BuildStatus.Success,
                $"{fit.Candidates.Count} candidate model(s) across {analysis.Chains.Count} segment chain(s)",
                warnings.Concat(fit.Notices).ToList()),
            analysis.Chains, fit.Candidates, Fingerprint(inputs), fit.SamplesUsed, false);
    }

    private static string Describe(string code) => code switch
    {
        "SEMANTIC-INVERTED-AGE" => "older age appears above younger age inside an unbroken segment",
        _ => code
    };

    private static InputFingerprint Fingerprint(BuildInputs inputs)
    {
        var parts = inputs.Project.Anchors
            .Select(a => $"{a.Id}:{a.Depth.Meters:R}:{a.MeasuredAge.Kind}:{a.MeasuredAge.Mean():R}")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var joined = string.Join("|", parts);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return new InputFingerprint(hash, inputs.Parameters.Version, parts);
    }
}
