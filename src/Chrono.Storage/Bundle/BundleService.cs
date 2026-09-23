using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Events;

namespace Chrono.Storage.Bundle;

public enum BundleStatus { Ok, FormatCorrupt, SemanticMismatch }

public sealed record BundleManifest(
    string Format,
    string Version,
    string ProjectId,
    string ProjectName,
    string CreatedAtUtc,
    string RuleSetVersion,
    IReadOnlyList<BundleBranchRef> Branches,
    string Sha256);

public sealed record BundleBranchRef(string BranchId, long EventCount, string EventsFile);

public sealed record BundleResult(BundleStatus Status, string? Error, int EventCount);

public static class BundleService
{
    private const string FormatName = "chrono-core-bundle";
    private const string VersionName = "1.0";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Export(ProjectStore store, string projectId, string projectName,
        string ruleSetVersion, string outputPath)
    {
        var branches = store.ListBranches();
        var refs = new List<BundleBranchRef>();
        var tempDir = Path.Combine(Path.GetTempPath(), "chrono-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var payloadDir = Path.Combine(tempDir, "events");
            Directory.CreateDirectory(payloadDir);
            foreach (var branch in branches)
            {
                var events = store.ReadEvents(branch);
                var fileName = $"events/{branch}.json";
                File.WriteAllText(Path.Join(tempDir, fileName),
                    JsonSerializer.Serialize(events, Options), Encoding.UTF8);
                refs.Add(new BundleBranchRef(branch, events.Count, fileName));
            }

            var manifest = new BundleManifest(FormatName, VersionName, projectId, projectName,
                DateTimeOffset.UtcNow.ToString("O"), ruleSetVersion, refs, "pending");
            var body = JsonSerializer.Serialize(manifest with { Sha256 = "" }, Options);
            var sha = Convert.ToHexString(SHA(body)).ToLowerInvariant();
            File.WriteAllText(Path.Join(tempDir, "manifest.json"),
                JsonSerializer.Serialize(manifest with { Sha256 = sha }, Options), Encoding.UTF8);

            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempDir, outputPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    public static BundleResult Import(ProjectStore store, string bundlePath, string importId)
    {
        store.Initialize();
        MarkImport(store, importId, "started", null);
        try
        {
            if (!File.Exists(bundlePath))
            {
                MarkImport(store, importId, "failed", "bundle file missing");
                return new BundleResult(BundleStatus.FormatCorrupt, "bundle file missing", 0);
            }

            string tempDir;
            try
            {
                tempDir = Extract(bundlePath);
            }
            catch (InvalidDataException ex)
            {
                MarkImport(store, importId, "failed", "corrupt archive: " + ex.Message);
                return new BundleResult(BundleStatus.FormatCorrupt, "corrupt archive", 0);
            }

            try
            {
                var manifestPath = Path.Join(tempDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    MarkImport(store, importId, "failed", "manifest missing");
                    return new BundleResult(BundleStatus.FormatCorrupt, "manifest missing", 0);
                }
                var manifestText = File.ReadAllText(manifestPath, Encoding.UTF8);
                BundleManifest? manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<BundleManifest>(manifestText, Options);
                }
                catch (JsonException ex)
                {
                    MarkImport(store, importId, "failed", "manifest malformed: " + ex.Message);
                    return new BundleResult(BundleStatus.FormatCorrupt, "manifest malformed", 0);
                }
                if (manifest == null || manifest.Format != FormatName)
                {
                    MarkImport(store, importId, "failed", "unknown bundle format");
                    return new BundleResult(BundleStatus.FormatCorrupt, "unknown format", 0);
                }

                var expected = manifest with { Sha256 = "" };
                var expectedText = JsonSerializer.Serialize(expected, Options);
                if (!FixedTimeEquals(SHA(expectedText), FromHex(manifest.Sha256)))
                {
                    MarkImport(store, importId, "failed", "manifest checksum mismatch");
                    return new BundleResult(BundleStatus.FormatCorrupt, "checksum mismatch", 0);
                }

                var loaded = new List<(BundleBranchRef Ref, List<ProjectEvent> Events)>();
                foreach (var branchRef in manifest.Branches)
                {
                    var eventPath = Path.Join(tempDir, branchRef.EventsFile);
                    if (!File.Exists(eventPath))
                    {
                        MarkImport(store, importId, "failed", $"missing {branchRef.EventsFile}");
                        return new BundleResult(BundleStatus.FormatCorrupt, "missing branch events", 0);
                    }
                    try
                    {
                        var events = JsonSerializer.Deserialize<List<ProjectEvent>>(
                            File.ReadAllText(eventPath, Encoding.UTF8), Options);
                        if (events == null)
                            return new BundleResult(BundleStatus.FormatCorrupt, "null events", 0);
                        var ordered = events.OrderBy(e => e.Sequence).ToList();
                        for (int i = 0; i < ordered.Count; i++)
                        {
                            if (ordered[i].Sequence != i + 1 ||
                                ordered[i].ProjectId != manifest.ProjectId ||
                                ordered[i].BranchId != branchRef.BranchId)
                            {
                                MarkImport(store, importId, "failed", "event identity/order mismatch");
                                return new BundleResult(BundleStatus.SemanticMismatch,
                                    "event identity/order mismatch", 0);
                            }
                        }
                        loaded.Add((branchRef, ordered));
                    }
                    catch (JsonException ex)
                    {
                        MarkImport(store, importId, "failed", "events malformed: " + ex.Message);
                        return new BundleResult(BundleStatus.FormatCorrupt, "events malformed", 0);
                    }
                }

                int total = loaded.Sum(x => x.Events.Count);
                ApplyAtomically(store, manifest, loaded);
                MarkImport(store, importId, "completed", null);
                return new BundleResult(BundleStatus.Ok, null, total);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch (Exception ex)
        {
            MarkImport(store, importId, "failed", ex.Message);
            throw;
        }
    }

    public static string ImportState(ProjectStore store, string importId)
    {
        using var conn = store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM imports WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", importId);
        return cmd.ExecuteScalar() as string ?? "unknown";
    }

    private static void ApplyAtomically(ProjectStore store, BundleManifest manifest,
        List<(BundleBranchRef Ref, List<ProjectEvent> Events)> loaded)
    {
        using var conn = store.Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM derived_cache");
        Exec(conn, tx, "DELETE FROM material_snapshots");
        Exec(conn, tx, "DELETE FROM events");
        Exec(conn, tx, "DELETE FROM branches");
        Exec(conn, tx, "DELETE FROM projects");
        Exec(conn, tx, "INSERT INTO projects(id,name,created_at_utc) VALUES($id,$n,$t)",
            ("$id", manifest.ProjectId), ("$n", manifest.ProjectName), ("$t", manifest.CreatedAtUtc));
        foreach (var (refx, events) in loaded)
        {
            Exec(conn, tx,
                "INSERT INTO branches(id,project_id,parent_branch_id,created_at_utc,frozen) " +
                "VALUES($id,$p,NULL,$t,$f)",
                ("$id", refx.BranchId), ("$p", manifest.ProjectId),
                ("$t", manifest.CreatedAtUtc),
                ("$f", refx.BranchId == "main" ? 0L : 0L));
            foreach (var e in events)
            {
                Exec(conn, tx,
                    "INSERT INTO events(id,project_id,branch_id,sequence,parent_event_ids,kind," +
                    "payload_json,actor,rule_set_version,created_at_utc,label,frozen) VALUES(" +
                    "$id,$p,$b,$s,$par,$k,$pl,$a,$r,$ts,$l,$f)",
                    ("$id", e.Id), ("$p", e.ProjectId), ("$b", e.BranchId), ("$s", e.Sequence),
                    ("$par", JsonSerializer.Serialize(e.ParentEventIds)),
                    ("$k", e.Kind.ToString()), ("$pl", e.PayloadJson),
                    ("$a", e.Actor), ("$r", e.RuleSetVersion),
                    ("$ts", e.CreatedAtUtc.ToString("O")),
                    ("$l", (object?)e.Label ?? DBNull.Value),
                    ("$f", e.Frozen ? 1L : 0L));
            }
        }
        tx.Commit();
    }

    private static void MarkImport(ProjectStore store, string importId, string state, string? error)
    {
        using var conn = store.Open();
        using var tx = conn.BeginTransaction();
        if (state == "started")
        {
            Exec(conn, tx,
                "INSERT INTO imports(id,project_id,started_at_utc,state,error) VALUES($id,'',datetime('now'),$s,$e)",
                ("$id", importId), ("$s", state), ("$e", (object?)error ?? DBNull.Value));
        }
        else
        {
            Exec(conn, tx,
                "UPDATE imports SET state=$s,error=$e,completed_at_utc=CASE WHEN $s='completed' OR $s='failed' THEN datetime('now') ELSE completed_at_utc END WHERE id=$id",
                ("$id", importId), ("$s", state), ("$e", (object?)error ?? DBNull.Value));
        }
        tx.Commit();
    }

    private static string Extract(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chrono-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ZipFile.ExtractToDirectory(path, dir);
        return dir;
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string sql, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value is DBNull ? DBNull.Value : value);
        cmd.ExecuteNonQuery();
    }

    private static byte[] SHA(string text) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    private static bool FixedTimeEquals(byte[] a, byte[] b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}
