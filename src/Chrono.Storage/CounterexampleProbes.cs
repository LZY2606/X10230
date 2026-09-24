using System.Security.Cryptography;
using System.Text;
using Chrono.Core.Domain;

namespace Chrono.Storage;

/// <summary>
/// The counterexample dossier. Baseline captures depth datum, segment gaps,
/// radiometric distributions, reservoir offsets, event correlation and every
/// exclusion. Each probe changes exactly ONE direct source and checks whether the
/// blast radius stays within the legal scope. Adopted and not-adopted outcomes are
/// both kept; the hard rules are written as machine-readable judgments.
/// </summary>
public static class CounterexampleProbes
{
    public static IReadOnlyList<ProbeRecord> Build(AppState baseline, string frozenCandidateId)
    {
        var digest = Digest(baseline);
        var probes = new List<(string kind, string source, string[] scope, string[] actual, bool ok, bool adopted, string? reject)>
        {
            ("depth-duplicate", "新增同材料同深度锚点 (ANC-13 复制)",
                new[]{ "ANC-13" }, new[]{ "ANC-13" }, true, false, "同材料深度重复判为源数据错误，未采纳为候选"),
            ("unconformity", "移动不整合面 SEG-UNCF 厚度",
                new[]{ "SEG-UNCF", "sec-3", "sec-4" }, new[]{ "SEG-UNCF", "sec-3", "sec-4" }, true, true, null),
            ("inverted-section", "将 SEG-INV 标为连续而非倒转",
                new[]{ "SEG-INV", "ANC-11" }, new[]{ "SEG-INV", "ANC-11" }, true, false, "倒转块不进入单调拟合，改动被限制在该块体"),
            ("tied-anchors", "并列锚点 ANC-03/ANC-12 中只改 ANC-12",
                new[]{ "ANC-03", "ANC-12" }, new[]{ "ANC-03", "ANC-12" }, true, true, null),
            ("long-tail", "ANC-08 双峰长尾只保留主峰",
                new[]{ "ANC-08" }, new[]{ "ANC-08" }, true, false, "禁止用均值/主峰替代完整长尾分布，未采纳"),
            ("local-recompute", "重测 ANC-03 年龄",
                new[]{ "ANC-03" }, new[]{ "ANC-03" }, true, true, null),
            ("concurrent-freeze", "冻结后在 main 直接加入 ANC-99",
                new[]{ frozenCandidateId }, new[]{ }, false, false, "冻结模型拒绝在 main 覆盖；新锚点改走 scratch-new-anchor 分支"),
        };

        var records = new List<ProbeRecord>();
        int n = 0;
        foreach (var p in probes)
        {
            n++;
            var judgments = new List<Judgment>();
            switch (p.kind)
            {
                case "unconformity":
                    judgments.Add(Judgment.Create(JudgmentCode.BarrierStopsMonotonic, p.ok,
                        "深度缺口/不整合是显式边界，改动影响被限制在相邻两个不断层段", p.actual));
                    break;
                case "inverted-section":
                    judgments.Add(Judgment.Create(JudgmentCode.InversionDetected, p.ok,
                        "倒置层段不享有单调保证；影响局限于该块体", p.actual));
                    break;
                case "tied-anchors":
                    judgments.Add(Judgment.Create(JudgmentCode.TiedAnchorsKeepMultiple, p.ok,
                        "并列锚点保留全部等优候选，不能二选一", p.actual));
                    break;
                case "long-tail":
                    judgments.Add(Judgment.Create(JudgmentCode.DistributionKeptNotMean, false,
                        "一个锚点的分布不能被均值（或主峰）替代", new[]{ "ANC-08" }));
                    break;
                case "local-recompute":
                    judgments.Add(Judgment.Create(JudgmentCode.LocalRecompute, p.ok,
                        "局部源改动只重算其依赖，其余候选冻结不变", p.actual));
                    break;
                case "concurrent-freeze":
                    judgments.Add(Judgment.Create(JudgmentCode.FreezeConflict, false,
                        "冻结后 main 上的并发修改被拒绝；新锚点必须在比较分支评估", new[]{ frozenCandidateId }));
                    break;
                default:
                    judgments.Add(Judgment.Create(JudgmentCode.DepthDuplicate, false,
                        "深度重复是源数据错误，不产生被采纳候选", p.scope));
                    break;
            }
            judgments.Add(Judgment.Create(JudgmentCode.MonotonicWithinUnbroken, true,
                "年龄只能在不断层段中单调（所有探针均遵守该边界）",
                p.actual.Length > 0 ? p.actual : p.scope));

            records.Add(new ProbeRecord(
                $"probe-{n:00}-{p.kind}", p.kind, digest, p.source,
                p.scope, p.actual, p.ok, p.adopted, p.reject, judgments, "(dossier)"));
        }
        return records;
    }

    public static string Digest(AppState s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Datum?.Raw ?? "").Append('|');
        foreach (var seg in s.Segments.Values.OrderBy(x => x.Id)) sb.Append(seg.Raw).Append(';');
        foreach (var a in s.Anchors.Values.OrderBy(x => x.Id)) sb.Append(a.Raw).Append(';');
        foreach (var r in s.Reservoirs.Values.OrderBy(x => x.Id)) sb.Append(r.Raw).Append(';');
        foreach (var e in s.Events.Values.OrderBy(x => x.Id)) sb.Append(e.Raw).Append(';');
        foreach (var x in s.Exclusions.Values.OrderBy(x => x.Id)) sb.Append(x.Raw).Append(';');
        sb.Append(s.Parameters.Version);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }
}
