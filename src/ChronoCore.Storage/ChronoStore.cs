using System.Text.Json;
using ChronoCore.Domain.Engine;
using ChronoCore.Domain.Model;
using Microsoft.Data.Sqlite;

namespace ChronoCore.Storage;

/// <summary>SQLite-backed store. Writes are transactional; WAL plus a
/// startup integrity check provide half-write recovery: an interrupted write
/// rolls back cleanly and the previous committed state remains readable.
/// Business-layer projections are injected, so the store is unit-testable.</summary>
public sealed class ChronoStore : IDisposable
{
    private readonly SqliteConnection _cn;
    private readonly string _connectionString;
    public string ConnectionStringForImport => _connectionString;
    public const string AppActor = "sedimentology-team";

    public ChronoStore(string connectionString)
    {
        _connectionString = connectionString;
        _cn = new SqliteConnection(connectionString);
        _cn.Open();
        Initialize();
    }

    public static string FileConnection(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

    private void Initialize()
    {
        using var pragma = _cn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();

        foreach (var stmt in Schema.Ddl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(stmt) || stmt.StartsWith("PRAGMA")) continue;
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
        using var meta = _cn.CreateCommand();
        meta.CommandText = "INSERT OR IGNORE INTO meta(key,value) VALUES('schema', $v)";
        meta.Parameters.AddWithValue("$v", Schema.Version);
        meta.ExecuteNonQuery();
    }

    /// <summary>Quick integrity check used after a suspected half-write.</summary>
    public bool QuickCheck()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase);
    }

    // ---- packages -----------------------------------------------------------

    public StoredPackage SavePackage(string caseId, string version, string sourceId, string rawText, CorePackage pkg)
    {
        var digest = AgeModelEngine.Digest(pkg);
        var packageId = "PKG-" + digest + "-" + Stable.Short(caseId + version + sourceId);
        using var tx = _cn.BeginTransaction();
        var ordinal = ScalarInt("SELECT COALESCE(MAX(ordinal)+1,0) FROM packages", tx) ?? 0;
        Exec("""
            INSERT OR IGNORE INTO packages
            (package_id,case_id,case_version,source_id,raw_text,input_digest,imported_at,ordinal)
            VALUES($id,$c,$cv,$s,$raw,$d,$at,$o)
            """, tx,
            ("$id", packageId), ("$c", caseId), ("$cv", version), ("$s", sourceId),
            ("$raw", rawText), ("$d", digest), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$o", ordinal));
        if (Changes(tx) == 0) tx.Rollback(); else tx.Commit();
        return new StoredPackage(packageId, digest);
    }

    public record StoredPackage(string PackageId, string Digest);

    private int Changes(SqliteTransaction tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT changes()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- events -------------------------------------------------------------

    public IReadOnlyList<ChronoEvent> Events(string branchId)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT event_id,branch_id,parent_event_id,kind,material_package_id,ruleset_version,input_digest,payload_json,actor,created_at,seq,frozen_candidate_id FROM events WHERE branch_id=$b ORDER BY seq";
        cmd.Parameters.AddWithValue("$b", branchId);
        var list = new List<ChronoEvent>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(ReadEvent(rd));
        return list;
    }

    public IReadOnlyList<ChronoEvent> AllEvents()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT event_id,branch_id,parent_event_id,kind,material_package_id,ruleset_version,input_digest,payload_json,actor,created_at,seq,frozen_candidate_id FROM events ORDER BY seq";
        var list = new List<ChronoEvent>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(ReadEvent(rd));
        return list;
    }

    private static ChronoEvent ReadEvent(SqliteDataReader rd) => new(
        rd.GetString(0), Enum.Parse<EventKind>(rd.GetString(3)), rd.GetString(1),
        rd.IsDBNull(2) ? null : rd.GetString(2),
        rd.IsDBNull(4) ? null : rd.GetString(4),
        rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8),
        DateTimeOffset.Parse(rd.GetString(9)), rd.GetInt64(10),
        rd.IsDBNull(11) ? null : rd.GetString(11));

    /// <summary>Append an event. Freeze conflict is raised as a distinct
    /// exception (semantic) rather than a generic DB failure.</summary>
    public ChronoEvent AppendEvent(EventKind kind, string branchId, string? parentEventId,
        string? packageId, string rulesetVersion, string inputDigest, object payload,
        string actor = AppActor, string? frozenCandidateId = null,
        int? expectedLockVersion = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload, Replay.JsonOpts);
        using var tx = _cn.BeginTransaction();

        if (kind is EventKind.DerivationRun or EventKind.SourceAmended or EventKind.ParameterAdjusted)
        {
            var frozen = GetFreeze(branchId, tx);
            if (frozen is not null)
                throw new FrozenModelException(branchId, frozen.Value.CandidateId,
                    "分支模型已冻结；改变基底/规则/源数据只能在比较视图中进行，不能覆盖冻结结论");
        }
        if (kind == EventKind.CandidateFrozen)
        {
            var frozen = GetFreeze(branchId, tx);
            if (frozen is not null && expectedLockVersion is not null &&
                frozen.Value.LockVersion != expectedLockVersion)
                throw new ConcurrencyConflictException(branchId, frozen.Value.LockVersion,
                    "冻结锁版本不匹配：存在并发操作，请重读最新状态后再决定");
        }

        var eventId = "EVT-" + Stable.Short(Guid.NewGuid().ToString("N") + DateTimeOffset.UtcNow.Ticks);
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events
            (event_id,branch_id,parent_event_id,kind,material_package_id,ruleset_version,input_digest,
             payload_json,actor,created_at,frozen_candidate_id)
            VALUES($eid,$b,$p,$k,$m,$r,$d,$pl,$a,$at,$f)
            """;
        Add(cmd, ("$eid", eventId), ("$b", branchId), ("$p", (object?)parentEventId ?? DBNull.Value),
            ("$k", kind.ToString()), ("$m", (object?)packageId ?? DBNull.Value),
            ("$r", rulesetVersion), ("$d", inputDigest), ("$pl", payloadJson),
            ("$a", actor), ("$at", DateTimeOffset.UtcNow.ToString("O")),
            ("$f", (object?)frozenCandidateId ?? DBNull.Value));
        cmd.ExecuteNonQuery();
        var seq = LastSeq(tx);

        if (kind == EventKind.CandidateFrozen && frozenCandidateId is not null)
        {
            using var fc = _cn.CreateCommand();
            fc.Transaction = tx;
            fc.CommandText = """
                INSERT INTO freezes(branch_id,candidate_id,input_digest,ruleset_fingerprint,lock_version,created_event_id)
                VALUES($b,$c,$d,$r,$v,$e)
                ON CONFLICT(branch_id) DO UPDATE SET candidate_id=$c,input_digest=$d,
                  ruleset_fingerprint=$r,lock_version=lock_version+1,created_event_id=$e
                """;
            Add(fc, ("$b", branchId), ("$c", frozenCandidateId), ("$d", inputDigest),
                ("$r", rulesetVersion), ("$v", expectedLockVersion ?? 1), ("$e", eventId));
            fc.ExecuteNonQuery();
        }
        tx.Commit();
        return new ChronoEvent(eventId, kind, branchId, parentEventId, packageId, rulesetVersion,
            inputDigest, payloadJson, actor, DateTimeOffset.UtcNow, seq, frozenCandidateId);
    }

    public (string CandidateId, int LockVersion, string Digest, string Ruleset)? GetFreeze(string branchId)
    {
        using var tx = _cn.BeginTransaction();
        var f = GetFreeze(branchId, tx);
        tx.Commit();
        return f;
    }

    private (string CandidateId, int LockVersion, string Digest, string Ruleset)? GetFreeze(
        string branchId, SqliteTransaction tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT candidate_id,lock_version,input_digest,ruleset_fingerprint FROM freezes WHERE branch_id=$b";
        cmd.Parameters.AddWithValue("$b", branchId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return (rd.GetString(0), rd.GetInt32(1), rd.GetString(2), rd.GetString(3));
    }

    private long LastSeq(SqliteTransaction tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- derivations --------------------------------------------------------

    public void SaveDerivation(string derivationId, string eventId, string packageId,
        EngineResult result, string parentDependency)
    {
        using var tx = _cn.BeginTransaction();
        Exec("""
            INSERT OR IGNORE INTO derivations
            (derivation_id,event_id,package_id,input_digest,ruleset_fingerprint,parent_dependency,result_json,created_at)
            VALUES($id,$e,$p,$d,$rf,$par,$json,$at)
            """, tx,
            ("$id", derivationId), ("$e", eventId), ("$p", packageId),
            ("$d", result.InputDigest), ("$rf", result.RulesetFingerprint),
            ("$par", parentDependency),
            ("$json", JsonSerializer.Serialize(result, Replay.JsonOpts)),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }

    // ---- counterexample archive --------------------------------------------

    public void SaveCounterfactual(CounterfactualRecord rec)
    {
        using var tx = _cn.BeginTransaction();
        Exec("""
            INSERT OR REPLACE INTO counterfactuals
            (archive_id,branch_id,label,baseline_pkg_id,changed_pkg_id,changed_material,change_kind,
             report_json,adopted_json,rejected_json,created_at)
            VALUES($id,$b,$l,$bp,$cp,$m,$k,$rj,$aj,$xj,$at)
            """, tx,
            ("$id", rec.ArchiveId), ("$b", rec.BranchId), ("$l", rec.Label),
            ("$bp", rec.BaselinePackageId), ("$cp", rec.ChangedPackageId),
            ("$m", rec.ChangedMaterial), ("$k", rec.ChangeKind),
            ("$rj", JsonSerializer.Serialize(rec.Report, Replay.JsonOpts)),
            ("$aj", JsonSerializer.Serialize(rec.AdoptedOutcomeIds, Replay.JsonOpts)),
            ("$xj", JsonSerializer.Serialize(rec.RejectedOutcomeIds, Replay.JsonOpts)),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }

    public IReadOnlyList<CounterfactualRecord> ListCounterfactuals()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT archive_id,branch_id,label,baseline_pkg_id,changed_pkg_id,changed_material,change_kind,report_json,adopted_json,rejected_json FROM counterfactuals ORDER BY rowid";
        var list = new List<CounterfactualRecord>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var report = JsonSerializer.Deserialize<ImpactReport>(rd.GetString(7), Replay.JsonOpts)!;
            var adopted = JsonSerializer.Deserialize<List<string>>(rd.GetString(8), Replay.JsonOpts)!;
            var rejected = JsonSerializer.Deserialize<List<string>>(rd.GetString(9), Replay.JsonOpts)!;
            list.Add(new CounterfactualRecord(rd.GetString(0), rd.GetString(1), rd.GetString(2),
                rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6),
                report, adopted, rejected));
        }
        return list;
    }

    public string? RawText(string packageId)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT raw_text FROM packages WHERE package_id=$id";
        cmd.Parameters.AddWithValue("$id", packageId);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<(string PackageId, string CaseId, string Version, string Digest, string Raw)> ListPackages()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT package_id,case_id,case_version,input_digest,raw_text FROM packages ORDER BY ordinal";
        var list = new List<(string, string, string, string, string)>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4)));
        return list;
    }

    // ---- helpers ------------------------------------------------------------

    private long? ScalarInt(string sql, SqliteTransaction tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql;
        return cmd.ExecuteScalar() as long?;
    }

    private void Exec(string sql, SqliteTransaction tx, params (string Name, object Value)[] ps)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql;
        Add(cmd, ps);
        cmd.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand cmd, params (string Name, object Value)[] ps)
    {
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    public void Dispose() => _cn.Dispose();
}

public sealed record CounterfactualRecord(
    string ArchiveId, string BranchId, string Label,
    string BaselinePackageId, string ChangedPackageId,
    string ChangedMaterial, string ChangeKind,
    ImpactReport Report,
    IReadOnlyList<string> AdoptedOutcomeIds,
    IReadOnlyList<string> RejectedOutcomeIds);

public sealed class FrozenModelException : Exception
{
    public string BranchId { get; }
    public string CandidateId { get; }
    public FrozenModelException(string branch, string candidate, string message) : base(message)
    { BranchId = branch; CandidateId = candidate; }
}

public sealed class ConcurrencyConflictException : Exception
{
    public string BranchId { get; }
    public int CurrentVersion { get; }
    public ConcurrencyConflictException(string branch, int version, string message) : base(message)
    { BranchId = branch; CurrentVersion = version; }
}
