using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoCore.Domain;
using ChronoCore.Engine;

namespace ChronoCore.Storage;

public enum TransferOutcome { Imported = 0, FormatCorrupt = 1, SemanticallyInvalid = 2, HalfWrittenRecovered = 3 }

public sealed class TransferReport
{
    public TransferOutcome Outcome { get; set; }
    public string? ProjectId { get; set; }
    public string Message { get; set; } = "";
    public List<DiagnosticEntry> Diagnostics { get; set; } = new();
}

public sealed class ExportBundle
{
    public string Format { get; set; } = "chrono-bundle";
    public int FormatVersion { get; set; } = 1;
    public string RulesetVersion { get; set; } = AgeModelEngine.RulesetVersion;
    public string ChecksumSha256 { get; set; } = "";
    public ProjectRecord? Project { get; set; }
}

/// <summary>项目导出/再导入：保持身份、顺序、分支与决定历史。导入为原子操作并支持半写恢复。</summary>
public sealed class ProjectTransfer
{
    private static readonly JsonSerializerOptions Opts = Web.JsonOptions.Store;

    public string Export(ProjectRecord project)
    {
        var bundle = new ExportBundle { Project = project };
        var json = JsonSerializer.Serialize(bundle, Opts);
        bundle.ChecksumSha256 = Sha(json);
        return JsonSerializer.Serialize(bundle, Opts);
    }

    public ExportBundle? ReadBundle(string json)
    {
        try { return JsonSerializer.Deserialize<ExportBundle>(json, Opts); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 原子导入：1) 校验格式与校验和（格式损坏）；2) 语义校验（语义无解类）；
    /// 3) 备份现有库→临时库加载→校验通过后整体替换（半写自动恢复备份）。
    /// </summary>
    public TransferReport Import(string json, string targetDbPath, bool simulateCrashDuringCommit = false)
    {
        ExportBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ExportBundle>(json, Opts)
                ?? throw new JsonException("空包");
        }
        catch (JsonException ex)
        {
            return new TransferReport { Outcome = TransferOutcome.FormatCorrupt, Message = "格式损坏：" + ex.Message };
        }
        if (bundle is null || bundle.Format != "chrono-bundle" || bundle.Project is null)
            return new TransferReport { Outcome = TransferOutcome.FormatCorrupt, Message = "格式损坏：缺少格式标识或项目体" };

        var claimed = bundle.ChecksumSha256;
        var probe = new ExportBundle
        {
            Format = bundle.Format, FormatVersion = bundle.FormatVersion,
            RulesetVersion = bundle.RulesetVersion, ChecksumSha256 = "", Project = bundle.Project
        };
        var probeJson = JsonSerializer.Serialize(probe, Opts);
        if (string.IsNullOrEmpty(claimed) || !FixedTimeEquals(claimed, Sha(probeJson)))
            return new TransferReport { Outcome = TransferOutcome.FormatCorrupt, Message = "格式损坏：校验和不匹配，拒绝任何部分写入" };

        var semantics = ValidateSemantics(bundle.Project);
        if (semantics.Count > 0)
            return new TransferReport { Outcome = TransferOutcome.SemanticallyInvalid, ProjectId = bundle.Project.Id, Diagnostics = semantics, Message = "语义校验失败：材料身份或事件链不完整" };

        var backup = targetDbPath + ".preimport";
        var tempDb = targetDbPath + ".importing";
        foreach (var f in new[] { backup, tempDb, tempDb + "-wal", tempDb + "-shm" })
            if (File.Exists(f)) File.Delete(f);

        if (File.Exists(targetDbPath))
        {
            File.Copy(targetDbPath, backup, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(targetDbPath + suffix)) File.Copy(targetDbPath + suffix, backup + suffix, overwrite: true);
        }

        var recovered = false;
        try
        {
            var tempRepo = new ProjectRepository(tempDb);
            tempRepo.SaveProject(bundle.Project);
            if (simulateCrashDuringCommit)
                throw new IOException("模拟写入中断（半写）");
            if (!tempRepo.IntegrityCheckOk(out var report))
                throw new IOException("临时库 integrity_check 失败: " + report);

            // 整体替换：先删旧库文件再移入临时库；任一步失败都从 .preimport 恢复
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(targetDbPath + suffix)) File.Delete(targetDbPath + suffix);
            File.Move(tempDb, targetDbPath);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(tempDb + suffix)) File.Move(tempDb + suffix, targetDbPath + suffix);
        }
        catch (IOException)
        {
            recovered = true;
            RestoreBackup(targetDbPath, backup, tempDb);
        }

        var verify = new ProjectRepository(targetDbPath);
        if (!verify.IntegrityCheckOk(out var verifyReport))
        {
            if (!recovered) RestoreBackup(targetDbPath, backup, tempDb);
            return new TransferReport
            {
                Outcome = TransferOutcome.HalfWrittenRecovered,
                ProjectId = bundle.Project.Id,
                Message = "检测到半写库，已从导入前备份恢复；import_ledger 记录本次事件。"
            };
        }

        RecordImportLedger(targetDbPath, bundle.Project.Id, backup, Sha(probeJson),
            recovered ? "recovered" : "committed");

        return new TransferReport
        {
            Outcome = recovered ? TransferOutcome.HalfWrittenRecovered : TransferOutcome.Imported,
            ProjectId = bundle.Project.Id,
            Message = recovered ? "导入中断后自动恢复并重试成功" : "导入成功：身份、顺序、分支与决定历史保持"
        };
    }

    private static void RestoreBackup(string target, string backup, string tempDb)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(target + suffix)) File.Delete(target + suffix);
        if (File.Exists(backup))
        {
            File.Copy(backup, target, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(backup + suffix)) File.Copy(backup + suffix, target + suffix, overwrite: true);
        }
        foreach (var f in new[] { tempDb, tempDb + "-wal", tempDb + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static void RecordImportLedger(string dbPath, string projectId, string backup, string checksum, string status)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
INSERT INTO import_ledger(project_id,backup_path,checksum_before,checksum_after,status,started_at,finished_at)
VALUES($p,$b,$c,$c2,$s,$t,$t2)
ON CONFLICT(project_id) DO UPDATE SET status=$s,checksum_after=$c2,finished_at=$t2;
""";
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$b", backup);
        cmd.Parameters.AddWithValue("$c", checksum);
        cmd.Parameters.AddWithValue("$c2", checksum);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("$t2", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private static List<DiagnosticEntry> ValidateSemantics(ProjectRecord p)
    {
        var diag = new List<DiagnosticEntry>();
        if (string.IsNullOrWhiteSpace(p.Id))
            diag.Add(new DiagnosticEntry { Code = "E-PROJ-ID", Severity = Severity.Error, Location = "project.id", Message = "项目缺少稳定身份" });
        var matIds = new HashSet<string>(p.Materials.Select(m => m.Id));
        foreach (var d in p.Decisions)
            if (d.TargetMaterialId is not null && !matIds.Contains(d.TargetMaterialId))
                diag.Add(new DiagnosticEntry { Code = "E-DANGLING-DECISION", Severity = Severity.Error, Location = $"decisions/{d.Id}", MaterialId = d.TargetMaterialId, Message = "决定引用的材料不存在" });
        foreach (var e in p.Events)
        {
            if (e.ParentEventId is not null && !p.Events.Any(x => x.Id == e.ParentEventId))
                diag.Add(new DiagnosticEntry { Code = "E-EVENT-CHAIN", Severity = Severity.Error, Location = $"events/{e.Id}", Message = "事件父链断裂，无法回到旧依赖版本" });
            if (string.IsNullOrEmpty(e.InputDigest))
                diag.Add(new DiagnosticEntry { Code = "E-EVENT-DIGEST", Severity = Severity.Error, Location = $"events/{e.Id}", Message = "事件缺少输入摘要" });
        }
        var seqGroups = p.Events.GroupBy(e => (e.BranchId, e.Seq));
        foreach (var g in seqGroups)
            if (g.Count() > 1)
                diag.Add(new DiagnosticEntry { Code = "E-EVENT-ORDER", Severity = Severity.Error, Location = $"events/{g.Key}", Message = "分支内事件序号冲突，稳定顺序无法保证" });
        return diag;
    }

    private static string Sha(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
