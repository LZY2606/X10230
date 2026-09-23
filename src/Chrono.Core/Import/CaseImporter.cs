using System.Globalization;
using System.Text.Json;
using Chrono.Core.Domain;

namespace Chrono.Core.Import;

// 三种结果严格分开：FormatCorrupt（JSON/结构损坏）、SemanticIssues（可解析但有语义问题）、Ok。
public enum ImportOutcome { Imported, FormatCorrupt, SemanticIssues }

public sealed record ImportReport
{
    public required ImportOutcome Outcome { get; init; }
    public CasePackage? Case { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
}

public static class CaseImporter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static ImportReport Import(string rawJson, string origin = "input.json")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            return Corrupt(origin, "JSON_PARSE", ex.Message, (int)(ex.LineNumber ?? 0) + 1,
                (int)(ex.BytePositionInLine ?? 0));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return Corrupt(origin, "JSON_PARSE", ex.Message, 0, 0);
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("caseId", out _) ||
            !root.TryGetProperty("anchors", out _))
        {
            return new ImportReport
            {
                Outcome = ImportOutcome.FormatCorrupt,
                Diagnostics = { Diag("STRUCTURE", origin, "缺少必需顶层字段 caseId/anchors；这不是领域案例包。",
                    Severity: DiagnosticSeverity.Error) }
            };
        }

        var diags = new List<Diagnostic>();
        CasePackage? pkg = null;
        try
        {
            pkg = root.Deserialize<CasePackage>(JsonOpts);
        }
        catch (JsonException ex)
        {
            return new ImportReport
            {
                Outcome = ImportOutcome.FormatCorrupt,
                Diagnostics = { Diag("STRUCTURE", origin, ex.Message, (int)(ex.LineNumber ?? 0) + 1,
                    Severity: DiagnosticSeverity.Error) }
            };
        }
        if (pkg is null)
            return new ImportReport
            {
                Outcome = ImportOutcome.FormatCorrupt,
                Diagnostics = { Diag("STRUCTURE", origin, "案例包反序列化得到 null。", Severity: DiagnosticSeverity.Error) }
            };

        NormalizeAll(pkg, origin, diags);

        // 所有材料必须携带单位/基准。缺失是语义问题，不静默补默认值。
        CheckMandatory(pkg, diags);

        bool hasErrors = diags.Exists(d => d.Severity == DiagnosticSeverity.Error);
        return new ImportReport
        {
            Outcome = hasErrors ? ImportOutcome.SemanticIssues : ImportOutcome.Imported,
            Case = pkg,
            Diagnostics = diags
        };
    }

    private static ImportReport Corrupt(string file, string code, string msg, int line, int off) => new()
    {
        Outcome = ImportOutcome.FormatCorrupt,
        Diagnostics =
        {
            new Diagnostic
            {
                Code = code, Severity = DiagnosticSeverity.Error, Message = msg, ObjectId = "",
                Location = new SourceLocation { File = file, Line = line, Offset = off }
            }
        }
    };

    private static Diagnostic Diag(string code, string obj, string msg, int line = 0,
        DiagnosticSeverity Severity = DiagnosticSeverity.Warning, string? field = null, string? raw = null) => new()
    {
        Code = code, Severity = Severity, Message = msg, ObjectId = obj, Field = field,
        Location = line > 0 ? new SourceLocation { File = "input.json", Line = line } : null,
        RawFragment = raw
    };

    private static void NormalizeAll(CasePackage pkg, string origin, List<Diagnostic> diags)
    {
        foreach (var a in pkg.Anchors)
        {
            NormalizeQ(a.Depth, a.Id, nameof(AgeAnchor.Depth), diags, "m");
            NormalizeDistribution(a.Age, a.Id, diags);
            if (a.Source is null)
                diags.Add(Diag("SOURCE_MISSING", a.Id, "锚点缺少来源位置，证据链不完整。",
                    Severity: DiagnosticSeverity.Error));
        }
        foreach (var e in pkg.EventLayers)
        {
            NormalizeQ(e.Depth, e.Id, nameof(EventLayer.Depth), diags, "m");
            NormalizeDistribution(e.PublishedAge, e.Id, diags);
        }
        foreach (var s in pkg.Segments)
        {
            NormalizeQ(s.TopDepth, s.Id, nameof(Segment.TopDepth), diags, "m");
            NormalizeQ(s.BottomDepth, s.Id, nameof(Segment.BottomDepth), diags, "m");
            if (s.TopDepth.IsUsable && s.BottomDepth.IsUsable && s.TopDepth.Value! >= s.BottomDepth.Value!)
                diags.Add(Diag("SEGMENT_INVERTED", s.Id,
                    $"分段 {s.Id} 顶深 {s.TopDepth.Value} 不浅于底深 {s.BottomDepth.Value}。",
                    Severity: DiagnosticSeverity.Error, field: "segments"));
        }
        foreach (var b in pkg.Boundaries.Where(b => b.GapEstimate is not null))
            NormalizeQ(b.GapEstimate!, b.Id, nameof(Boundary.GapEstimate), diags, "m");

        // 储层偏移必须能被引用到
        foreach (var a in pkg.Anchors.Where(a => !string.IsNullOrEmpty(a.ReservoirOffsetId)))
            if (pkg.ReservoirOffsets.All(r => r.Id != a.ReservoirOffsetId))
                diags.Add(Diag("RESERVOIR_REF_DANGLING", a.Id,
                    $"锚点引用了不存在的储层偏移 {a.ReservoirOffsetId}。",
                    Severity: DiagnosticSeverity.Error, field: nameof(AgeAnchor.ReservoirOffsetId)));
    }

    private static void CheckMandatory(CasePackage pkg, List<Diagnostic> diags)
    {
        if (pkg.Datums.Count == 0)
            diags.Add(Diag("DATUM_MISSING", pkg.CaseId, "案例没有任何深度基准。",
                Severity: DiagnosticSeverity.Error));
        if (pkg.Segments.Count == 0)
            diags.Add(Diag("SEGMENTS_MISSING", pkg.CaseId, "案例没有任何岩芯分段。",
                Severity: DiagnosticSeverity.Error));

        foreach (var a in pkg.Anchors)
        {
            if (string.IsNullOrEmpty(a.Depth.Unit))
                diags.Add(Diag("UNIT_MISSING", a.Id, "锚点深度缺少单位。",
                    Severity: DiagnosticSeverity.Error, field: nameof(AgeAnchor.Depth)));
            if (string.IsNullOrEmpty(a.Depth.Basis))
                diags.Add(Diag("BASIS_MISSING", a.Id, "锚点深度缺少坐标基准（如 mbsf）。",
                    Severity: DiagnosticSeverity.Error, field: nameof(AgeAnchor.Depth)));
            if (string.IsNullOrEmpty(a.Age.Unit) || string.IsNullOrEmpty(a.Age.Basis))
                diags.Add(Diag("TIME_BASIS_MISSING", a.Id, "年龄分布缺少单位或时间基准（如 cal yr BP / IntCal20）。",
                    Severity: DiagnosticSeverity.Error, field: nameof(AgeAnchor.Age)));
        }
    }

    private static void NormalizeQ(QValue q, string objId, string field, List<Diagnostic> diags, string expectUnit)
    {
        if (q.Value.HasValue)
        {
            if (!string.IsNullOrEmpty(q.Unit) && q.Unit != expectUnit && q.Unit != "cm" && q.Unit != "m")
                diags.Add(Diag("UNIT_UNKNOWN", objId, $"无法把单位 '{q.Unit}' 转换到 {expectUnit}，保留原始表示。",
                    field: field, raw: q.Raw));
            return;
        }
        var raw = q.Raw?.Trim() ?? "";
        string unit = q.Unit ?? expectUnit;
        string num = raw;
        string? detectedUnit = null;
        foreach (var u in new[] { "mbsf", "mCD", "m", "cm" })
            if (raw.EndsWith(u, StringComparison.OrdinalIgnoreCase))
            {
                detectedUnit = u;
                num = raw[..^u.Length].Trim();
                break;
            }

        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            double meters = detectedUnit == "cm" ? v / 100.0 : v;
            // 非破坏式规范化：Raw 不动，仅派生层补 Value。
            typeof(QValue).GetProperty(nameof(QValue.Value))!.SetValue(q, meters);
            typeof(QValue).GetProperty(nameof(QValue.Unit))!.SetValue(q, "m");
            if (string.IsNullOrEmpty(q.Basis) && detectedUnit is "mbsf" or "mCD")
                typeof(QValue).GetProperty(nameof(QValue.Basis))!.SetValue(q, detectedUnit);
        }
        else
        {
            typeof(QValue).GetProperty(nameof(QValue.Diagnostic))!.SetValue(q,
                $"无法把 '{raw}' 解析为带单位深度；保留原位，不参与绘图/推导。");
            diags.Add(Diag("UNCONVERTIBLE_VALUE", objId,
                $"字段 {field} 的原始值 '{raw}' 无法转换，已留在原位置并附诊断。", field: field, raw: raw));
        }
    }

    private static void NormalizeDistribution(AgeDistribution d, string objId, List<Diagnostic> diags)
    {
        if (!d.IsUsable)
        {
            typeof(AgeDistribution).GetProperty(nameof(AgeDistribution.Diagnostic))!.SetValue(d,
                string.IsNullOrEmpty(d.Diagnostic) ? "年龄分布不可用（既无正态参数也无样本）。" : d.Diagnostic);
            diags.Add(Diag("DISTRIBUTION_UNUSABLE", objId,
                $"年龄分布 '{d.Raw}' 不可用，保留原位，不以均值伪装。",
                Severity: DiagnosticSeverity.Error, raw: d.Raw));
            return;
        }
        if (d.Kind == DistributionKind.Samples && d.Samples is { Length: >= 8 })
        {
            var s = (double[])d.Samples.Clone();
            Array.Sort(s);
            double q50 = Quantile(s, .5), q95 = Quantile(s, .95), q05 = Quantile(s, .05);
            double iqr = Quantile(s, .75) - Quantile(s, .25);
            bool tail = q95 - q50 > 2.2 * (q50 - q05) + iqr;
            if (tail)
                typeof(AgeDistribution).GetProperty(nameof(AgeDistribution.LongTail))!.SetValue(d, true);
        }
    }

    private static double Quantile(double[] sorted, double p)
    {
        double pos = p * (sorted.Length - 1);
        int i = (int)Math.Floor(pos);
        double f = pos - i;
        return i >= sorted.Length - 1 ? sorted[^1] : sorted[i] * (1 - f) + sorted[i + 1] * f;
    }
}
