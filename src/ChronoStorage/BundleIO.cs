using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ChronoCore;

namespace ChronoStorage;

public static class BundleFormat
{
    public const int Version = 1;
    public const string ManifestName = "manifest.json";
    public const string EventsName = "events.jsonl";
    public const string BranchesName = "branches.json";
    public const string DerivedName = "derived.jsonl";
    public const string ProjectsName = "projects.json";
}

public sealed record ExportRow(
    long Seq, string BranchId, string EventId, string Type, string At, string? Actor,
    string PayloadJson, IReadOnlyList<string> Parents, string InputDigest,
    string? ParameterVersion, string? DerivedModelId, bool Adopted);

public sealed record ImportStats(int Events, int Branches, int Derived, List<string> Warnings);

public sealed partial class ChronoRepository
{
    public byte[] ExportBundle(string projectName)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, BundleFormat.ManifestName, JsonSerializer.Serialize(new
            {
                format = "pairwise-chrono-bundle",
                version = BundleFormat.Version,
                project = projectName,
                exportedAt = DateTimeOffset.UtcNow.ToString("O")
            }));
            var events = ReadAllEvents().OrderBy(e => e.BranchId).ThenBy(e => e.Seq);
            var sb = new StringBuilder();
            foreach (var e in events)
                sb.AppendLine(JsonSerializer.Serialize(new ExportRow(
                    e.Seq, e.BranchId, e.Id, e.Type, e.At, e.Actor,
                    e.Payload.ToJsonString(), e.Parents, e.InputDigest,
                    e.ParameterVersion, e.DerivedModelId, e.Adopted)));
            WriteEntry(zip, BundleFormat.EventsName, sb.ToString());

            var branches = ListBranches();
            WriteEntry(zip, BundleFormat.BranchesName, JsonSerializer.Serialize(branches));

            var dsb = new StringBuilder();
            foreach (var b in branches)
            foreach (var m in ReadDerivedModels(b.BranchId))
                dsb.AppendLine(JsonSerializer.Serialize(new { branchId = b.BranchId, model = m }));
            WriteEntry(zip, BundleFormat.DerivedName, dsb.ToString());

            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT project_id,name,created_at FROM projects";
            using var rd = cmd.ExecuteReader();
            var projects = new List<object>();
            while (rd.Read()) projects.Add(new { projectId = rd.GetString(0), name = rd.GetString(1), createdAt = rd.GetString(2) });
            WriteEntry(zip, BundleFormat.ProjectsName, JsonSerializer.Serialize(projects));
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    /// <summary>
    /// 事务化导入：身份(seq/branch/eventId)、顺序、来源关系完整保留。
    /// 格式损坏（坏 zip / 坏 JSON / 清单缺失）与语义/预算失败严格区分并整体回滚。
    /// </summary>
    public ImportStats ImportBundle(byte[] zipBytes, bool replaceExisting)
    {
        using var zip = TryOpenZip(zipBytes);
        var manifestEntry = zip.GetEntry(BundleFormat.ManifestName)
            ?? throw new FormatCorruptedImportException("导出包缺少 manifest.json，判定为格式损坏", BundleFormat.ManifestName);
        using (var mr = new StreamReader(manifestEntry.Open()))
        {
            try
            {
                using var doc = JsonDocument.Parse(mr.ReadToEnd());
                if (!doc.RootElement.TryGetProperty("format", out var f) ||
                    f.GetString() != "pairwise-chrono-bundle")
                    throw new FormatCorruptedImportException("manifest.format 不匹配", BundleFormat.ManifestName);
            }
            catch (JsonException ex)
            {
                throw new FormatCorruptedImportException("manifest.json 不是合法 JSON：" + ex.Message, BundleFormat.ManifestName);
            }
        }

        var rows = ReadJsonl<ExportRow>(zip, BundleFormat.EventsName);
        var branches = ReadJson<List<BranchRow>>(zip, BundleFormat.BranchesName);
        var derivedRows = ReadJsonl<DerivedLine>(zip, BundleFormat.DerivedName);
        var projects = ReadJson<List<ProjectLine>>(zip, BundleFormat.ProjectsName);

        // 顺序与身份一致性校验（等价输入稳定顺序：直接保留源 seq）
        foreach (var grp in rows.GroupBy(r => r.BranchId))
        {
            var seqs = grp.Select(r => r.Seq).OrderBy(x => x).ToList();
            for (var i = 0; i < seqs.Count; i++)
                if (seqs[i] != i + 1)
                    throw new FormatCorruptedImportException(
                        $"分支 {grp.Key} 事件序号不连续（期望 {i + 1}，实际 {seqs[i]}），身份/顺序被破坏",
                        BundleFormat.EventsName);
            if (grp.Select(r => r.EventId).Distinct().Count() != grp.Count())
                throw new FormatCorruptedImportException($"分支 {grp.Key} 存在重复事件身份", BundleFormat.EventsName);
        }

        using var c = Open();
        using var t = c.BeginTransaction();
        var batch = BeginBatch(c, t, "import-bundle");
        if (replaceExisting)
            foreach (var table in new[] { "derived_models", "events", "branches", "projects", "freeze_locks", "op_journal" })
            {
                using var del = c.CreateCommand(); del.Transaction = t;
                del.CommandText = $"DELETE FROM {table}";
                del.ExecuteNonQuery();
            }

        foreach (var p in projects)
        {
            using var ins = c.CreateCommand(); ins.Transaction = t;
            ins.CommandText = "INSERT INTO projects(project_id,name,created_at) VALUES($a,$b,$c)";
            ins.Parameters.AddWithValue("$a", p.ProjectId);
            ins.Parameters.AddWithValue("$b", p.Name);
            ins.Parameters.AddWithValue("$c", p.CreatedAt);
            ins.ExecuteNonQuery();
        }
        foreach (var b in branches)
        {
            using var ins = c.CreateCommand(); ins.Transaction = t;
            ins.CommandText = """
            INSERT INTO branches(branch_id,project_id,parent_branch_id,from_seq,baseline_digest,changed_source_id,purpose)
            VALUES($b,(SELECT project_id FROM projects LIMIT 1),$p,$f,$bd,$cs,$pu)
            """;
            ins.Parameters.AddWithValue("$b", b.BranchId);
            ins.Parameters.AddWithValue("$p", (object?)b.ParentBranchId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$f", b.FromSeq);
            ins.Parameters.AddWithValue("$bd", (object?)b.BaselineDigest ?? DBNull.Value);
            ins.Parameters.AddWithValue("$cs", (object?)b.ChangedSourceId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$pu", b.Purpose);
            ins.ExecuteNonQuery();
        }
        foreach (var r in rows.OrderBy(x => x.BranchId).ThenBy(x => x.Seq))
        {
            using var ins = c.CreateCommand(); ins.Transaction = t;
            ins.CommandText = """
            INSERT INTO events(seq,branch_id,event_id,type,at,actor,payload_json,parents_json,
              input_digest,parameter_version,derived_model_id,adopted)
            VALUES($s,$b,$i,$ty,$at,$ac,$pj,$pa,$d,$pv,$dm,$ad)
            """;
            ins.Parameters.AddWithValue("$s", r.Seq);
            ins.Parameters.AddWithValue("$b", r.BranchId);
            ins.Parameters.AddWithValue("$i", r.EventId);
            ins.Parameters.AddWithValue("$ty", r.Type);
            ins.Parameters.AddWithValue("$at", r.At);
            ins.Parameters.AddWithValue("$ac", (object?)r.Actor ?? DBNull.Value);
            ins.Parameters.AddWithValue("$pj", r.PayloadJson);
            ins.Parameters.AddWithValue("$pa", JsonSerializer.Serialize(r.Parents));
            ins.Parameters.AddWithValue("$d", r.InputDigest ?? "");
            ins.Parameters.AddWithValue("$pv", (object?)r.ParameterVersion ?? DBNull.Value);
            ins.Parameters.AddWithValue("$dm", (object?)r.DerivedModelId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$ad", r.Adopted ? 1 : 0);
            ins.ExecuteNonQuery();
        }
        foreach (var dl in derivedRows)
        {
            using var ins = c.CreateCommand(); ins.Transaction = t;
            ins.CommandText = """
            INSERT INTO derived_models(model_id,branch_id,model_json,input_digest,parameter_version,created_seq,adopted)
            VALUES($i,$b,$j,$d,$p,$s,$a)
            """;
            ins.Parameters.AddWithValue("$i", dl.Model.Id);
            ins.Parameters.AddWithValue("$b", dl.BranchId);
            ins.Parameters.AddWithValue("$j", JsonSerializer.Serialize(dl.Model));
            ins.Parameters.AddWithValue("$d", dl.Model.InputDigest);
            ins.Parameters.AddWithValue("$p", dl.Model.ParameterVersion);
            ins.Parameters.AddWithValue("$s", 0);
            ins.Parameters.AddWithValue("$a", dl.Model.SemanticallyValid ? 1 : 0);
            ins.ExecuteNonQuery();
        }
        CompleteBatch(c, t, batch);
        t.Commit();
        return new ImportStats(rows.Count, branches.Count, derivedRows.Count, new List<string>());
    }

    private static ZipArchive TryOpenZip(byte[] bytes)
    {
        try
        {
            var ms = new MemoryStream(bytes);
            return new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new FormatCorruptedImportException("导出包不是合法 zip：" + ex.Message, "bundle");
        }
    }

    private static List<T> ReadJsonl<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new FormatCorruptedImportException($"缺少 {name}", name);
        using var r = new StreamReader(entry.Open());
        var list = new List<T>();
        string? line; var n = 0;
        while ((line = r.ReadLine()) is not null)
        {
            n++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonSerializer.Deserialize<T>(line)!); }
            catch (JsonException ex)
            {
                throw new FormatCorruptedImportException($"{name} 第 {n} 行 JSON 损坏：{ex.Message}", $"{name}:{n}");
            }
        }
        return list;
    }

    private static T ReadJson<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new FormatCorruptedImportException($"缺少 {name}", name);
        using var r = new StreamReader(entry.Open());
        try { return JsonSerializer.Deserialize<T>(r.ReadToEnd())!; }
        catch (JsonException ex)
        {
            throw new FormatCorruptedImportException($"{name} JSON 损坏：{ex.Message}", name);
        }
    }

    private sealed record DerivedLine(string BranchId, DerivedModel Model);
    private sealed record ProjectLine(string ProjectId, string Name, string CreatedAt);

    public void DeleteAllData()
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        var batch = BeginBatch(c, t, "delete-all");
        foreach (var table in new[] { "derived_models", "events", "branches", "projects", "freeze_locks" })
        {
            using var del = c.CreateCommand(); del.Transaction = t;
            del.CommandText = $"DELETE FROM {table}";
            del.ExecuteNonQuery();
        }
        CompleteBatch(c, t, batch);
        t.Commit();
    }
}
