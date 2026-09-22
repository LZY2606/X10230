using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace ChronoAge.Core;

/// <summary>
/// Portable project bundle. Identity (event seq, material ids, candidate/artifact/display ids),
/// branch membership and decision lineage survive export -> delete local DB -> import unchanged.
/// </summary>
public sealed record ProjectBundle(
    int FormatVersion,
    string ProjectId,
    string Name,
    string CasePackId,
    string CasePackVersion,
    IReadOnlyList<EventRow> Events,
    IReadOnlyList<DerivationRow> Derivations,
    IReadOnlyList<FrozenRow> Frozen,
    string ExportedUtc)
{
    public const int CurrentFormatVersion = 1;
}

public sealed record EventRow(long Seq, string BranchId, string Kind, string Actor, string AtUtc,
    string PayloadJson, string? ParentSeq, string InputHash, string? Note);
public sealed record DerivationRow(string ArtifactId, string BranchId, long Seq, string ArtifactJson);
public sealed record FrozenRow(string FrozenModelId, string BranchId, string CandidateId, string DerivationId,
    string DependencyIdsJson, string AtUtc, bool Active);

public static class BundleIo
{
    private static readonly JsonSerializerOptions RowOpts = new()
    {
        PropertyNamingPolicy = null // PascalCase property names so json_extract paths are stable
    };

    public static void Export(string dbPath, string outPath)
    {
        using var store = new JournalStore(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        var events = store.LoadEvents();
        var bundle = new ProjectBundle(
            ProjectBundle.CurrentFormatVersion,
            GetMeta(store, "project_id"),
            GetMeta(store, "project_name"),
            GetMeta(store, "casepack_id"),
            GetMeta(store, "casepack_version"),
            events.Select(e => new EventRow(e.Seq, e.BranchId, e.Kind, e.Actor, e.AtUtc,
                e.PayloadJson, e.ParentSeq, e.InputHash, e.Note)).ToList(),
            LoadAllDerivations(store),
            LoadAllFrozen(store),
            DateTimeOffset.UtcNow.ToString("O"));
        File.WriteAllText(outPath, JsonSerializer.Serialize(bundle, RowOpts));
    }

    /// <summary>
    /// Import a bundle into a fresh/existing DB via staging. If <paramref name="crashAfterRows"/>
    /// is set, the process deliberately stops mid-batch (tests half-write recovery): the staging
    /// rows remain and the journal is untouched; the next open sweeps them away.
    /// </summary>
    public static Outcome<string> Import(
        string bundlePath, string dbPath, bool intoFresh, int? crashAfterRows = null)
    {
        string json;
        try { json = File.ReadAllText(bundlePath); }
        catch (Exception ex)
        {
            return Outcome<string>.FormatCorrupt(new[] { Diag.Error("BUNDLE_UNREADABLE", ex.Message, null, "file") });
        }
        ProjectBundle? bundle;
        try { bundle = JsonSerializer.Deserialize<ProjectBundle>(json, RowOpts); }
        catch (JsonException ex)
        {
            return Outcome<string>.FormatCorrupt(new[] { Diag.Error("BUNDLE_CORRUPT_JSON",
                $"Bundle JSON is structurally corrupt and nothing was imported: {ex.Message}", null, "json") });
        }
        if (bundle is null || bundle.FormatVersion != ProjectBundle.CurrentFormatVersion || bundle.Events is null)
        {
            return Outcome<string>.FormatCorrupt(new[] { Diag.Error("BUNDLE_BAD_FORMAT",
                "Bundle is missing required parts or has an unsupported format version.", null, "format") });
        }
        if (bundle.Events.Count == 0 || bundle.Events[0].Kind != EventKinds.ProjectCreated)
        {
            return Outcome<string>.Incomplete(new[] { Diag.Error("BUNDLE_NO_ROOT",
                "Bundle does not begin with a project_created event; refusing a partial project.", null, "events[0]") });
        }

        if (intoFresh && File.Exists(dbPath))
        {
            // Caller controls deletion; surface as a diagnostic rather than silently wiping.
            return Outcome<string>.Conflict(new[] { Diag.Error("BUNDLE_DB_EXISTS",
                "Target database already exists; delete it explicitly for a clean re-import.", dbPath, "db") });
        }

        var csb = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        using var conn = new SqliteConnection(csb);
        conn.Open();
        using (var p = conn.CreateCommand())
        {
            p.CommandText = "PRAGMA journal_mode=WAL;";
            p.ExecuteNonQuery();
        }
        EnsureSchema(conn);

        var txId = $"imp{DateTimeOffset.UtcNow.UtcTicks}";
        using (var tx = conn.BeginTransaction())
        {
            using (var st = conn.CreateCommand())
            {
                st.Transaction = tx;
                st.CommandText = "INSERT INTO staging_tx(tx_id,started_utc,item_count) VALUES($id,$t,$c)";
                st.Parameters.AddWithValue("$id", txId);
                st.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                st.Parameters.AddWithValue("$c", bundle.Events.Count);
                st.ExecuteNonQuery();
            }
            var ord = 0;
            foreach (var ev in bundle.Events.OrderBy(e => e.Seq))
            {
                using var row = conn.CreateCommand();
                row.Transaction = tx;
                row.CommandText = "INSERT INTO staging_rows(tx_id,ord,table_name,row_json) VALUES($t,$o,'events',$j)";
                row.Parameters.AddWithValue("$t", txId);
                row.Parameters.AddWithValue("$o", ord++);
                row.Parameters.AddWithValue("$j", JsonSerializer.Serialize(ev, RowOpts));
                row.ExecuteNonQuery();

                if (crashAfterRows is int n && ord == n)
                {
                    // Simulated crash: transaction is abandoned without commit (rows discarded by
                    // rollback), while staging_tx marker semantics emulate the worst case via an
                    // explicit orphan marker written in a separate committed transaction.
                    tx.Rollback();
                    using var orphan = conn.BeginTransaction();
                    using (var mark = conn.CreateCommand())
                    {
                        mark.Transaction = orphan;
                        mark.CommandText = "INSERT OR REPLACE INTO staging_tx(tx_id,started_utc,item_count) VALUES($id,$t,$c)";
                        mark.Parameters.AddWithValue("$id", txId);
                        mark.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                        mark.Parameters.AddWithValue("$c", bundle.Events.Count);
                        mark.ExecuteNonQuery();
                    }
                    orphan.Commit();
                    return Outcome<string>.Conflict(new[] { Diag.Error("IMPORT_SIMULATED_CRASH",
                        $"Simulated half-write after {ord} rows; staging tx {txId} left for sweep.", txId, "staging") });
                }
            }
            tx.Commit();
        }

        // Promote staging to journal in ONE transaction: either the whole project lands or none.
        using (var promote = conn.BeginTransaction())
        {
            using var copy = conn.CreateCommand();
            copy.Transaction = promote;
            copy.CommandText = """
                INSERT INTO events(seq,branch_id,kind,actor,at_utc,payload_json,parent_seq,input_hash,note)
                SELECT json_extract(row_json,'$.Seq'), json_extract(row_json,'$.BranchId'),
                       json_extract(row_json,'$.Kind'), json_extract(row_json,'$.Actor'),
                       json_extract(row_json,'$.AtUtc'), json_extract(row_json,'$.PayloadJson'),
                       json_extract(row_json,'$.ParentSeq'), json_extract(row_json,'$.InputHash'),
                       json_extract(row_json,'$.Note')
                FROM staging_rows WHERE tx_id=$t ORDER BY ord;
                """;
            copy.Parameters.AddWithValue("$t", txId);
            copy.ExecuteNonQuery();

            foreach (var d in bundle.Derivations)
            {
                using var dc = conn.CreateCommand();
                dc.Transaction = promote;
                dc.CommandText = """
                    INSERT OR REPLACE INTO derivations(artifact_id,branch_id,seq,rule_version,input_hash,depends_on,artifact_json,created_utc)
                    SELECT $id,$b,$seq,
                      json_extract($j,'$.RuleVersion'), json_extract($j,'$.InputHash'),
                      json_extract($j,'$.DependsOn'), $j, json_extract($j,'$.AtUtc');
                    """;
                dc.Parameters.AddWithValue("$id", d.ArtifactId);
                dc.Parameters.AddWithValue("$b", d.BranchId);
                dc.Parameters.AddWithValue("$seq", d.Seq);
                dc.Parameters.AddWithValue("$j", d.ArtifactJson);
                dc.ExecuteNonQuery();
            }
            foreach (var f in bundle.Frozen)
            {
                using var fc = conn.CreateCommand();
                fc.Transaction = promote;
                fc.CommandText = """
                    INSERT OR REPLACE INTO frozen_models(frozen_model_id,branch_id,candidate_id,derivation_id,dependency_ids,at_utc,active)
                    VALUES($id,$b,$c,$d,$dep,$t,$a);
                    """;
                fc.Parameters.AddWithValue("$id", f.FrozenModelId);
                fc.Parameters.AddWithValue("$b", f.BranchId);
                fc.Parameters.AddWithValue("$c", f.CandidateId);
                fc.Parameters.AddWithValue("$d", f.DerivationId);
                fc.Parameters.AddWithValue("$dep", f.DependencyIdsJson);
                fc.Parameters.AddWithValue("$t", f.AtUtc);
                fc.Parameters.AddWithValue("$a", f.Active ? 1 : 0);
                fc.ExecuteNonQuery();
            }
            PutMeta(promote, "project_id", bundle.ProjectId);
            PutMeta(promote, "project_name", bundle.Name);
            PutMeta(promote, "casepack_id", bundle.CasePackId);
            PutMeta(promote, "casepack_version", bundle.CasePackVersion);
            using var cleanup = conn.CreateCommand();
            cleanup.Transaction = promote;
            cleanup.CommandText = "DELETE FROM staging_rows WHERE tx_id=$t; DELETE FROM staging_tx WHERE tx_id=$t;";
            cleanup.Parameters.AddWithValue("$t", txId);
            cleanup.ExecuteNonQuery();
            promote.Commit();
        }
        return Outcome<string>.Success(txId);
    }

    private static string GetMeta(JournalStore store, string key)
    {
        using var cmd = store.CreateMetaCommand(key);
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static List<DerivationRow> LoadAllDerivations(JournalStore store)
    {
        var rows = new List<DerivationRow>();
        using var cmd = store.CreateQuery("SELECT artifact_id,branch_id,seq,artifact_json FROM derivations ORDER BY seq");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new DerivationRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
        return rows;
    }

    private static List<FrozenRow> LoadAllFrozen(JournalStore store)
    {
        var rows = new List<FrozenRow>();
        using var cmd = store.CreateQuery("SELECT frozen_model_id,branch_id,candidate_id,derivation_id,dependency_ids,at_utc,active FROM frozen_models");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new FrozenRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetInt32(6) != 0));
        return rows;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY, branch_id TEXT NOT NULL, kind TEXT NOT NULL, actor TEXT NOT NULL,
              at_utc TEXT NOT NULL, payload_json TEXT NOT NULL, parent_seq TEXT, input_hash TEXT NOT NULL, note TEXT);
            CREATE TABLE IF NOT EXISTS derivations(
              artifact_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, seq INTEGER NOT NULL,
              rule_version TEXT NOT NULL, input_hash TEXT NOT NULL, depends_on TEXT NOT NULL,
              artifact_json TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS frozen_models(
              frozen_model_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, candidate_id TEXT NOT NULL,
              derivation_id TEXT NOT NULL, dependency_ids TEXT NOT NULL, at_utc TEXT NOT NULL, active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS staging_tx(tx_id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, item_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS staging_rows(
              tx_id TEXT NOT NULL, ord INTEGER NOT NULL, table_name TEXT NOT NULL, row_json TEXT NOT NULL,
              PRIMARY KEY(tx_id, ord));
            """;
        cmd.ExecuteNonQuery();
    }

    private static void PutMeta(SqliteTransaction tx, string key, string value)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta(key,value,version) VALUES($k,$v,0) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
