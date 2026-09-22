using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChronoAge.Core;

/// <summary>
/// SQLite-backed event journal. Writes are serialized through an application lock and wrapped
/// in transactions; every mutation bumps <c>meta.version</c>. Imports are staged first and
/// committed atomically — a crash mid-write leaves a <c>staging_tx</c> marker which startup
/// sweeps, so half-written batches never leak into the journal.
/// </summary>
public sealed class JournalStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public JournalStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        Schema();
        SweepStaging();
    }

    private void Schema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              branch_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              actor TEXT NOT NULL,
              at_utc TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              parent_seq TEXT,
              input_hash TEXT NOT NULL,
              note TEXT,
              UNIQUE(branch_id, seq));
            CREATE TABLE IF NOT EXISTS derivations(
              artifact_id TEXT PRIMARY KEY,
              branch_id TEXT NOT NULL,
              seq INTEGER NOT NULL,
              rule_version TEXT NOT NULL,
              input_hash TEXT NOT NULL,
              depends_on TEXT NOT NULL,
              artifact_json TEXT NOT NULL,
              created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS frozen_models(
              frozen_model_id TEXT PRIMARY KEY,
              branch_id TEXT NOT NULL,
              candidate_id TEXT NOT NULL,
              derivation_id TEXT NOT NULL,
              dependency_ids TEXT NOT NULL,
              at_utc TEXT NOT NULL,
              active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS staging_tx(tx_id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, item_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS staging_rows(
              tx_id TEXT NOT NULL, ord INTEGER NOT NULL, table_name TEXT NOT NULL, row_json TEXT NOT NULL,
              PRIMARY KEY(tx_id, ord));
            """);
    }

    /// <summary>Recover from a half-written import: an unfinished staging batch is dropped wholesale.</summary>
    private void SweepStaging()
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var txIds = new List<string>();
            using (var q = _conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT tx_id FROM staging_tx";
                using var r = q.ExecuteReader();
                while (r.Read()) txIds.Add(r.GetString(0));
            }
            foreach (var id in txIds)
            {
                using var d = _conn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = "DELETE FROM staging_rows WHERE tx_id=$id; DELETE FROM staging_tx WHERE tx_id=$id;";
                d.Parameters.AddWithValue("$id", id);
                d.ExecuteNonQuery();
            }
            tx.Commit();
            StagingRecoveredCount = txIds.Count;
        }
    }

    public int StagingRecoveredCount { get; private set; }

    internal SqliteCommand CreateMetaCommand(string key)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd;
    }

    internal SqliteCommand CreateQuery(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    internal void PutProjectMeta(string projectId, string name, string casePackId, string casePackVersion)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (k, v) in new[]
            {
                ("project_id", projectId), ("project_name", name),
                ("casepack_id", casePackId), ("casepack_version", casePackVersion)
            })
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta(key,value,version) VALUES($k,$v,0) ON CONFLICT(key) DO UPDATE SET value=$v";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long CurrentVersion()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<EventRecord> LoadEvents(string? branchId = null, long? upToSeq = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            var sql = "SELECT seq,branch_id,kind,actor,at_utc,payload_json,parent_seq,input_hash,note FROM events";
            var where = new List<string>();
            if (branchId is not null) where.Add("branch_id=$b");
            if (upToSeq.HasValue) where.Add("seq<=$s");
            if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
            sql += " ORDER BY seq";
            cmd.CommandText = sql;
            if (branchId is not null) cmd.Parameters.AddWithValue("$b", branchId);
            if (upToSeq.HasValue) cmd.Parameters.AddWithValue("$s", upToSeq.Value);
            var list = new List<EventRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
            }
            return list;
        }
    }

    /// <summary>Append an event. If expectedVersion is given it enforces optimistic concurrency (freeze races).</summary>
    public Outcome<EventRecord> Append(
        string branchId, string kind, EventPayload payload, string actor, string inputHash,
        long? expectedVersion, string? note = null, string? parentSeq = null)
    {
        lock (_gate)
        {
            var cur = CurrentVersionNoLock();
            if (expectedVersion.HasValue && expectedVersion.Value != cur)
            {
                return Outcome<EventRecord>.Conflict(new[]
                {
                    Diag.Error("STORE_VERSION_CONFLICT",
                        $"The state moved (v{cur}) while you were working (expected v{expectedVersion.Value}); the write was rejected. Re-read and re-decide.",
                        null, "version")
                });
            }
            var frozen = AnyActiveFreezeNoLock(branchId);
            if (frozen is not null && IsMutating(kind))
            {
                return Outcome<EventRecord>.Conflict(new[]
                {
                    Diag.Error("STORE_FROZEN",
                        $"Model {frozen} is frozen on branch '{branchId}'; material/parameter changes are blocked until unfreeze or branch.",
                        frozen, "freeze")
                });
            }

            var json = JsonSerializer.Serialize(payload, ProjectState.JsonOpts);
            using var tx = _conn.BeginTransaction();
            long seq;
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO events(branch_id,kind,actor,at_utc,payload_json,parent_seq,input_hash,note)
                    VALUES($b,$k,$a,$t,$p,$ps,$h,$n);
                    SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("$b", branchId);
                ins.Parameters.AddWithValue("$k", kind);
                ins.Parameters.AddWithValue("$a", actor);
                ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                ins.Parameters.AddWithValue("$p", json);
                ins.Parameters.AddWithValue("$ps", (object?)parentSeq ?? DBNull.Value);
                ins.Parameters.AddWithValue("$h", inputHash);
                ins.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
                seq = (long)ins.ExecuteScalar()!;
            }
            BumpVersionNoLock(tx);
            tx.Commit();
            return Outcome<EventRecord>.Success(new EventRecord(seq, branchId, kind, actor,
                DateTimeOffset.UtcNow.ToString("O"), json, parentSeq, inputHash, note));
        }
    }

    /// <summary>Persist a derivation artifact alongside the event that produced it.</summary>
    public void SaveDerivation(string branchId, long seq, DerivationArtifact artifact)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO derivations
                  (artifact_id,branch_id,seq,rule_version,input_hash,depends_on,artifact_json,created_utc)
                VALUES($id,$b,$s,$rv,$ih,$dep,$j,$t);
                """;
            cmd.Parameters.AddWithValue("$id", artifact.ArtifactId);
            cmd.Parameters.AddWithValue("$b", branchId);
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.Parameters.AddWithValue("$rv", artifact.RuleVersion);
            cmd.Parameters.AddWithValue("$ih", artifact.InputHash);
            cmd.Parameters.AddWithValue("$dep", JsonSerializer.Serialize(artifact.DependsOn));
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(artifact, ProjectState.JsonOpts));
            cmd.Parameters.AddWithValue("$t", artifact.AtUtc);
            cmd.ExecuteNonQuery();
            BumpVersionNoLock(tx);
            tx.Commit();
        }
    }

    public DerivationArtifact? LoadDerivation(string artifactId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT artifact_json FROM derivations WHERE artifact_id=$id";
            cmd.Parameters.AddWithValue("$id", artifactId);
            var s = cmd.ExecuteScalar() as string;
            return s is null ? null : JsonSerializer.Deserialize<DerivationArtifact>(s, ProjectState.JsonOpts);
        }
    }

    public IReadOnlyList<DerivationArtifact> LoadAllDerivations(string branchId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT artifact_json FROM derivations WHERE branch_id=$b ORDER BY seq";
            cmd.Parameters.AddWithValue("$b", branchId);
            var list = new List<DerivationArtifact>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(JsonSerializer.Deserialize<DerivationArtifact>(r.GetString(0), ProjectState.JsonOpts)!);
            return list;
        }
    }

    public void MarkFrozen(ModelFrozen f, bool active, string branchId)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO frozen_models(frozen_model_id,branch_id,candidate_id,derivation_id,dependency_ids,at_utc,active)
                VALUES($id,$b,$c,$d,$dep,$t,$a)
                ON CONFLICT(frozen_model_id) DO UPDATE SET active=$a;
                """;
            cmd.Parameters.AddWithValue("$id", f.FrozenModelId);
            cmd.Parameters.AddWithValue("$b", branchId);
            cmd.Parameters.AddWithValue("$c", f.CandidateId);
            cmd.Parameters.AddWithValue("$d", f.DerivationId);
            cmd.Parameters.AddWithValue("$dep", JsonSerializer.Serialize(f.DependencyIds));
            cmd.Parameters.AddWithValue("$t", f.AtUtc);
            cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
            cmd.ExecuteNonQuery();
            BumpVersionNoLock(tx);
            tx.Commit();
        }
    }

    public void UnmarkFrozen(string id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE frozen_models SET active=0 WHERE frozen_model_id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private long CurrentVersionNoLock()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE((SELECT value FROM meta WHERE key='version'),'0')";
        return long.Parse((string)cmd.ExecuteScalar()!);
    }

    private static bool IsMutating(string kind) =>
        kind is EventKinds.MaterialImported or EventKinds.MaterialRevised or EventKinds.ParametersSet;

    private string? AnyActiveFreezeNoLock(string branchId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT frozen_model_id FROM frozen_models WHERE branch_id=$b AND active=1 LIMIT 1";
        cmd.Parameters.AddWithValue("$b", branchId);
        return cmd.ExecuteScalar() as string;
    }

    private static void BumpVersionNoLock(SqliteTransaction tx)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO meta(key,value,version) VALUES('version','1',1)
            ON CONFLICT(key) DO UPDATE SET version=version+1;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
