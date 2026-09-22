using System.Text.Json.Serialization;

namespace ChronoCore.Domain.Events;

public static class EventTypes
{
    public const string ProjectCreated = "project.created";
    public const string MaterialImported = "material.imported";
    public const string CoreDefined = "core.defined";
    public const string SegmentsImported = "segments.imported";
    public const string BoundariesImported = "boundaries.imported";
    public const string ReservoirImported = "reservoir.imported";
    public const string AnchorImported = "anchor.imported";
    public const string EventLayerImported = "eventlayer.imported";
    public const string ExclusionRecorded = "exclusion.recorded";
    public const string RuleSetVersioned = "ruleset.versioned";
    public const string ParameterRevised = "parameter.revised";
    public const string SourceDataChanged = "sourcedata.changed";
    public const string ManualDecision = "decision.manual";
    public const string CandidateDerived = "candidate.derived";
    public const string LocalRecompute = "recompute.local";
    public const string FreezeCreated = "freeze.created";
    public const string AnchorProjected = "anchor.projected";
    public const string DecisionAccepted = "decision.accepted";
    public const string DecisionRejected = "decision.rejected";
    public const string ScenarioReplayed = "scenario.replayed";
}

/// <summary>
/// 追加式事件：唯一结构化真相。每条事件记录输入摘要(canonical hash)、
/// 参数版本、父级依赖与链式指纹；旧事件永不被新计算覆盖。
/// </summary>
public sealed class ProjectEvent
{
    public required string Id { get; init; }
    public required long Seq { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Actor { get; init; }
    /// <summary>事件负载（领域对象 JSON，canonical 后参与摘要）。</summary>
    public required JsonElement Payload { get; init; }
    /// <summary>参数/规则版本（决定来自哪版规则）。</summary>
    public required string RuleSetId { get; init; }
    /// <summary>父级依赖：材料id、锚点id、模型id、前置事件id 等。</summary>
    public required IReadOnlyList<string> ParentRefs { get; init; }
    /// <summary>前一事件链式指纹。</summary>
    public required string PrevDigest { get; init; }
    /// <summary>本事件指纹 = sha256(prevDigest + canonical(payload) + seq...)。</summary>
    public required string Digest { get; init; }

    public static string ComputeDigest(string prevDigest, long seq, string type, JsonElement payload,
        string ruleSetId, IEnumerable<string> parents)
    {
        var canonical = Hashing.CanonicalJson(new SortedDictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = type,
            ["ruleSetId"] = ruleSetId,
            ["parents"] = parents.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ["payload"] = JsonToPlain(payload),
            ["prevDigest"] = prevDigest
        });
        return Hashing.Sha256Hex(canonical);
    }

    public static object? JsonToPlain(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonToPlain(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(JsonToPlain).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}

/// <summary>规则集版本（参数修订产生新版本，旧版本永久保留）。</summary>
public sealed record RuleSet
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    /// <summary>跨不整合/缺口是否允许插值（规则：不允许）。</summary>
    public bool InterpolateAcrossBoundaries { get; init; } = false;
    /// <summary>段内单调严格性（年龄随深度严格增加，极性=+1 时）。</summary>
    public bool StrictMonotonicWithinSegment { get; init; } = true;
    /// <summary>并列锚点最大枚举数（资源预算）。</summary>
    public int MaxTieCombinations { get; init; } = 64;
    /// <summary>蒙特卡洛样本数。</summary>
    public int MonteCarloN { get; init; } = 4001;
    /// <summary>储层缺省时是否报错而不是假定 0。</summary>
    public bool ReservoirRequiredForMarine { get; init; } = true;
    /// <summary>倒置层段内允许年龄随深度减小。</summary>
    public bool AllowInvertedPolarity { get; init; } = true;
    public string Note { get; init; } = "";
}
