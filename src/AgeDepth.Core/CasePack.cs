using System.Text.Json;

namespace AgeDepth.Core;

/// <summary>One scripted session event (decision / judgment / revision) in the case pack.</summary>
public sealed record CaseEvent(string Type, string PayloadJson);

/// <summary>
/// One counterexample: baseline preserved, exactly one direct source changed.
/// Both adopted and rejected outcomes are archived; judgments are machine-readable events.
/// </summary>
public sealed record CounterexampleScenario(
    string Id, string Title, string Change,
    List<AgeAnchor> AddAnchors, List<Section> AddSections, List<Boundary> AddBoundaries,
    List<string> RemoveAnchorIds, List<string> ExpectedDiagnostics,
    bool Adopted, string RejectedNote);

/// <summary>
/// Offline case pack: "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// Baseline keeps depth datum, segment gaps, radiocarbon distributions, reservoir offsets,
/// event-layer correlations and every exclusion decision.
/// </summary>
public sealed record CasePackData(
    string Format, string Name, string Narrative,
    List<Section> Sections, List<Boundary> Boundaries, List<AgeAnchor> Anchors,
    List<EventLayer> EventLayers, ModelParameters Parameters,
    List<string> InitiallyExcludedAnchorIds,
    List<CounterexampleScenario> Counterexamples)
{
    public const string ExpectedFormat = "agedepth-casepack";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static Result<CasePackData> FromJson(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<CasePackData>(json, JsonOptions);
            if (data is null || data.Format != ExpectedFormat)
                return Result.Failure<CasePackData>(ResultKind.FormatCorrupt,
                    new Diagnostic("casepack.format-corrupt", "Case pack marker missing or unknown.", "casepack:format"));
            return Result.Success(data);
        }
        catch (JsonException ex)
        {
            return Result.Failure<CasePackData>(ResultKind.FormatCorrupt,
                new Diagnostic("casepack.format-corrupt", $"Case pack JSON is corrupt: {ex.Message}", "casepack:body"));
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

public static class CasePack
{
    public const string Datum = "mbsf";
    public const string CoreId = "CORE-PD01";
    public const string ModelId = "model:CORE-PD01";

    public static CasePackData Build()
    {
        var sections = new List<Section>
        {
            new("sec:1", CoreId, 0.0, 12.4, Datum, "calcareous mud"),
            new("sec:2", CoreId, 12.4, 31.0, Datum, "silty clay"),
            new("sec:3", CoreId, 31.0, 48.6, Datum, "sandy silt"),
        };
        var boundaries = new List<Boundary>
        {
            new("bnd:hiatus-12.40", 12.4, "hiatus", Datum, "sedimentation gap, ~2 kyr missing"),
            new("bnd:unconformity-31.00", 31.0, "unconformity", Datum, "erosional unconformity"),
        };
        var anchors = new List<AgeAnchor>
        {
            new("A1", 2.10, Datum, new AgeDistribution("normal", 1850, 60, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A2", 6.35, Datum, new AgeDistribution("normal", 4200, 90, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A3", 9.80, Datum, new AgeDistribution("uniform", null, null, 6100, 6600, null, null, null, "yr BP"), 420, "yr", "marine shell", "Lab-B"),
            new("A4", 12.20, Datum, new AgeDistribution("longtail", null, null, null, null, 8.75, 0.12, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A5", 15.40, Datum, new AgeDistribution("normal", 9800, 150, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A6", 22.75, Datum, new AgeDistribution("empirical", null, null, null, null, null, null,
                new[] { 12800.0, 12950, 13010, 13120, 13040, 13200, 12900, 13150, 13080, 13300, 12980, 13060 }, "yr BP"), 400, "yr", "marine shell", "Lab-B"),
            new("A7", 30.60, Datum, new AgeDistribution("normal", 14500, 220, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A8", 35.20, Datum, new AgeDistribution("normal", 21200, 300, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A9", 44.90, Datum, new AgeDistribution("normal", 33400, 450, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A"),
            new("A10", 6.35, Datum, new AgeDistribution("normal", 5100, 260, null, null, null, null, null, "yr BP"), 0, "yr", "bulk sediment (replicate)", "Lab-C"),
        };
        var events = new List<EventLayer>
        {
            new("evt:tephra-7.30", 7.30, Datum, "tephra", 0.93, "regional tephra T-4"),
            new("evt:flood-18.90", 18.90, Datum, "flood layer", 0.81, "coarse silt lamina"),
            new("evt:tephra-33.10", 33.10, Datum, "tephra", 0.88, "regional tephra T-9"),
        };
        var parameters = new ModelParameters("v1", 400, 2000, 0.5);

        var counterexamples = new List<CounterexampleScenario>
        {
            new("cx:depth-duplicate", "深度重复",
                "Add anchor A11 at exactly the depth of A2 (6.35 mbsf).",
                new List<AgeAnchor> { new("A11", 6.35, Datum, new AgeDistribution("normal", 4250, 80, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A") },
                new List<Section>(), new List<Boundary>(), new List<string>(),
                new List<string> { "anchor.depth-duplicate" }, true, ""),
            new("cx:unconformity", "不整合",
                "Insert an additional unconformity at 22.0 mbsf splitting segment 2.",
                new List<AgeAnchor>(), new List<Section>(),
                new List<Boundary> { new("bnd:unconformity-22.00", 22.0, "unconformity", Datum, "counterexample split") },
                new List<string>(), new List<string>(), true, ""),
            new("cx:inverted-section", "倒置层段",
                "Add an inverted section (top 50.0, bottom 46.0) plus an anchor inside it.",
                new List<AgeAnchor> { new("A12", 48.0, Datum, new AgeDistribution("normal", 30000, 400, null, null, null, null, null, "yr BP"), 0, "yr", "plant", "Lab-A") },
                new List<Section> { new("sec:4", CoreId, 50.0, 46.0, Datum, "disturbed") },
                new List<Boundary>(), new List<string>(),
                new List<string> { "section.inverted", "anchor.outside-section" }, false,
                "Inverted section rejected: impact must stay outside continuous segments."),
            new("cx:tied-anchors", "并列锚点",
                "Add anchor A13 tied with A5 at 15.40 mbsf with a different distribution.",
                new List<AgeAnchor> { new("A13", 15.40, Datum, new AgeDistribution("uniform", null, null, 9500, 10100, null, null, null, "yr BP"), 0, "yr", "marine shell", "Lab-B") },
                new List<Section>(), new List<Boundary>(), new List<string>(),
                new List<string> { "anchor.depth-duplicate" }, true, ""),
            new("cx:long-tail", "年龄分布长尾",
                "Replace A7 with a long-tail (lognormal) distribution; skew must survive.",
                new List<AgeAnchor> { new("A7", 30.60, Datum, new AgeDistribution("longtail", null, null, null, null, 9.55, 0.35, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A") },
                new List<Section>(), new List<Boundary>(), new List<string>(),
                new List<string>(), true, ""),
            new("cx:local-recompute", "局部重算",
                "Revise anchor A1 (segment 1 only); segments 2 and 3 digests must be unchanged.",
                new List<AgeAnchor> { new("A1", 2.10, Datum, new AgeDistribution("normal", 1900, 55, null, null, null, null, null, "yr BP"), 0, "yr", "plant macrofossil", "Lab-A") },
                new List<Section>(), new List<Boundary>(), new List<string>(),
                new List<string>(), true, ""),
            new("cx:concurrent-freeze", "并发冻结",
                "Two reviewers freeze the same model version concurrently; exactly one wins.",
                new List<AgeAnchor>(), new List<Section>(), new List<Boundary>(), new List<string>(),
                new List<string>(), true, ""),
        };

        return new CasePackData(
            CasePackData.ExpectedFormat,
            "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系",
            "沉积学小组把岩芯分段、年龄锚点和事件层组成深度年龄模型，保留多套可辩护候选。",
            sections, boundaries, anchors, events, parameters,
            new List<string> { "A10" },
            counterexamples);
    }
}
