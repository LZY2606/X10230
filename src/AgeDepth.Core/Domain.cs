using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Core;

// ---------- 不可变材料层（sources）：导入后原样保留，永不就地修改 ----------

public static class SourceKinds
{
    public const string DepthDatum = "depth-datum";          // 深度基准
    public const string SegmentGap = "segment-gap";          // 分段缺口（岩芯缺失段）
    public const string Unconformity = "unconformity";       // 不整合
    public const string RadiometricAge = "radiometric-age";  // 放射性年龄（含完整分布）
    public const string ReservoirOffset = "reservoir-offset";// 储层偏移
    public const string EventLayer = "event-layer";          // 事件层相关
    public static readonly string[] All = { DepthDatum, SegmentGap, Unconformity, RadiometricAge, ReservoirOffset, EventLayer };
}

public sealed record SourceRecord(string Id, string Kind, string RawJson, string Hash, string BatchId, long EventSeq);

// ---------- 年龄分布：绝不允许用均值替代 ----------

public sealed record AgeDistribution(
    string Kind,                 // "samples" | "quantiles"
    double[]? Samples,           // 完整样本（长尾等形态全部保留）
    Dictionary<string, double>? Quantiles, // p2.5/p16/p50/p84/p97.5
    string Unit, string Datum)
{
    public double Median()
    {
        if (Samples is { Length: > 0 })
        {
            var s = Samples.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }
        return Quantiles!["p50"];
    }

    public double Quantile(double p)
    {
        if (Samples is { Length: > 0 })
        {
            var s = Samples.OrderBy(x => x).ToArray();
            var idx = Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1);
            return s[idx];
        }
        var key = p switch { <= 0.025 => "p2.5", <= 0.16 => "p16", <= 0.5 => "p50", <= 0.84 => "p84", _ => "p97.5" };
        return Quantiles![key];
    }

    /// <summary>确定性抽样：种子由输入摘要决定，保证等价输入得到稳定顺序与结果。</summary>
    public double[] DrawSamples(int n, Random rng)
    {
        var draws = new double[n];
        if (Samples is { Length: > 0 })
            for (var i = 0; i < n; i++) draws[i] = Samples[rng.Next(Samples.Length)];
        else
        {
            var qs = Quantiles!;
            var pts = new (double p, double v)[] {
                (0.025, qs["p2.5"]), (0.16, qs["p16"]), (0.5, qs["p50"]), (0.84, qs["p84"]), (0.975, qs["p97.5"]) };
            for (var i = 0; i < n; i++)
            {
                var u = 0.025 + rng.NextDouble() * 0.95;
                for (var k = 0; k < pts.Length - 1; k++)
                    if (u >= pts[k].p && u <= pts[k + 1].p)
                    {
                        var t = (u - pts[k].p) / (pts[k + 1].p - pts[k].p);
                        draws[i] = pts[k].v + t * (pts[k + 1].v - pts[k].v);
                        break;
                    }
            }
        }
        return draws;
    }
}

// ---------- 参数版本 ----------

public sealed record ModelParams(
    string Version,
    double DepthEpsilonM,       // 深度重复/并列判定阈值
    int MaxCandidates,          // 候选数预算
    int McSamples,              // 蒙特卡洛样本预算
    bool ApplyReservoirOffset,  // 是否应用储层偏移
    double EqualScoreTolerance) // 等优判定容差
{
    public static readonly ModelParams V1 = new("params-v1", 0.005, 64, 400, true, 1e-9);
}

// ---------- 事件层（append-only，机器可读） ----------

public static class EventKinds
{
    public const string ImportBatch = "import-batch";
    public const string ParamsRevised = "params-revised";
    public const string SourceSuperseded = "source-superseded";
    public const string ModelComputed = "model-computed";
    public const string ModelFrozen = "model-frozen";
    public const string FreezeConflict = "freeze-conflict";
    public const string AnchorAccepted = "anchor-accepted";
    public const string AnchorRejected = "anchor-rejected";
    public const string ExclusionDecision = "exclusion-decision";
    public const string ConflictRecorded = "conflict-recorded";
    public const string Recovery = "recovery";
    public const string Exported = "exported";
    public const string Imported = "imported";
}

public sealed record EventRecord(long Seq, string Id, string Kind, string PayloadJson,
    string[] Parents, string ParamVersion, string InputDigest, string CreatedAt)
{
    public JsonElement Payload() => JsonSerializer.Deserialize<JsonElement>(PayloadJson);
}

// ---------- 计算结果：三种失败是三种不同结果 ----------

public enum ComputeStatus { Ok, BudgetExhausted, CorruptFormat, Unsolvable }

public sealed record ComputationRecord(
    string Id, long EventSeq, ComputeStatus Status, string InputDigest, string ParamVersion,
    string[] Parents, string ResultJson, Diagnostic[] Diagnostics);

public sealed record CandidateRecord(string Id, string ComputationId, int Rank, double Score,
    bool Kept, string PayloadJson);

// ---------- 视图（由事件折叠得到；回到任意旧事件即折叠到该 seq） ----------

public sealed record AnchorView(
    string SourceId, string Kind, double DepthM, AgeDistribution Age, string Method,
    string? ReservoirOffsetRef, string RawJson, string State /* active|excluded|rejected|accepted|duplicate */);

public sealed record SegmentView(int Index, double TopM, double BaseM, string LowerBoundary /* gap|unconformity|core-base */);

public sealed record Snapshot(
    long UpToSeq,
    ModelParams Params,
    IReadOnlyList<SourceRecord> Sources,
    IReadOnlyList<AnchorView> Anchors,
    IReadOnlyList<SegmentView> Segments,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ComputationRecord> Computations,
    IReadOnlyList<CandidateRecord> Candidates,
    (string CandidateId, long EventSeq, int Version)? Freeze,
    IReadOnlyList<EventRecord> Events);

public static class Hashing
{
    public static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant()[..16];

    /// <summary>输入摘要：来源 id+hash 排序后拼接，保证等价输入摘要稳定。</summary>
    public static string InputDigest(IEnumerable<(string id, string hash)> parts, string paramVersion)
    {
        var joined = string.Join("|", parts.OrderBy(p => p.id, StringComparer.Ordinal).Select(p => $"{p.id}:{p.hash}"));
        return Sha256(paramVersion + ";" + joined);
    }
}
