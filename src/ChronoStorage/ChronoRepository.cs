using System.Data;
using System.Text.Json;
using ChronoCore;
using Microsoft.Data.Sqlite;

namespace ChronoStorage;

public sealed class HalfWriteRecoveredException(long batchId, string details) : Exception(details)
{
    public long BatchId { get; } = batchId;
    public string Details { get; } = details;
}

/// <summary>
/// SQLite 事件存储：事件流 + 不可变派生快照 + 半写日志。
/// 所有多表写入在一个事务里提交；崩溃恢复回滚未提交批次。
/// </summary>
public sealed partial class ChronoRepository
{
    private readonly string _cs;
    private static readonly JsonSerializerOptions Jo =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ChronoRepository(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var c = Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        Schema(c);
        var recovered = RecoverHalfWrites(c);
        if (recovered is not null) throw new HalfWriteRecoveredException(recovered.BatchId, recovered.Details);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Schema(SqliteConnection c)
    {
        Exec(c, """
        CREATE TABLE IF NOT EXISTS projects(
          project_id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS branches(
          branch_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_branch_id TEXT,
          from_seq INTEGER, baseline_digest TEXT, changed_source_id TEXT, purpose TEXT,
          UNIQUE(project_id, branch_id));
        CREATE TABLE IF NOT EXISTS events(
          seq INTEGER NOT NULL, branch_id TEXT NOT NULL, event_id TEXT NOT NULL,
          type TEXT NOT NULL, at TEXT NOT NULL, actor TEXT, payload_json TEXT NOT NULL,
          parents_json TEXT NOT NULL, input_digest TEXT, parameter_version TEXT,
          derived_model_id TEXT, adopted INTEGER NOT NULL DEFAULT 1,
          PRIMARY KEY(branch_id, seq));
        CREATE TABLE IF NOT EXISTS derived_models(
          model_id TEXT NOT NULL, branch_id TEXT NOT NULL, model_json TEXT NOT NULL,
          input_digest TEXT, parameter_version TEXT, created_seq INTEGER, adopted INTEGER DEFAULT 1,
          PRIMARY KEY(branch_id, model_id));
        CREATE TABLE IF NOT EXISTS freeze_locks(
          model_id TEXT PRIMARY KEY, holder_actor TEXT NOT NULL, held_since_event_id TEXT NOT NULL, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS op_journal(
          batch_id INTEGER PRIMARY KEY AUTOINCREMENT, state TEXT NOT NULL, note TEXT, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    public record RecoveryInfo(long BatchId, string Details);

    private static RecoveryInfo? RecoverHalfWrites(SqliteConnection c)
    {
        using var check = c.CreateCommand();
        check.CommandText = "SELECT batch_id, note FROM op_journal WHERE state='prepared' ORDER BY batch_id DESC LIMIT 1";
        using var rd = check.ExecuteReader();
        if (!rd.Read()) return null;
        var batch = rd.GetInt64(0); var note = rd.GetString(1);
        rd.Close();
        //  prepared 但没有 committed → 半写：整批回滚标记
        using var up = c.CreateCommand();
        up.CommandText = "UPDATE op_journal SET state='rolled-back' WHERE batch_id=$b";
        up.Parameters.AddWithValue("$b", batch);
        up.ExecuteNonQuery();
        return new RecoveryInfo(batch, $"检测到半写批次 {batch}（{note}），已回滚恢复");
    }

    /// <summary>测试钩子：制造一个遗留 prepared 批次用于半写恢复验收。</summary>
    public void InjectHalfWriteForRecovery(string note = "crash-injection")
    {
        using var c = Open();
        // 写入 prepared 记录并持久化（模拟进程在 CompleteBatch 之前崩溃），但绝不标记 committed
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO op_journal(state,note,at) VALUES('prepared',$n,$a)";
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void EnsureProject(string projectId, string name)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        using var ins = c.CreateCommand();
        ins.Transaction = t;
        ins.CommandText = "INSERT OR IGNORE INTO projects(project_id,name,created_at) VALUES($id,$n,$a)";
        ins.Parameters.AddWithValue("$id", projectId);
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
        ins.ExecuteNonQuery();
        using var br = c.CreateCommand();
        br.Transaction = t;
        br.CommandText = "INSERT OR IGNORE INTO branches(branch_id,project_id,parent_branch_id,from_seq,purpose) VALUES('main',$id,NULL,0,'基线')";
        br.Parameters.AddWithValue("$id", projectId);
        br.ExecuteNonQuery();
        t.Commit();
    }

    public bool ProjectExists()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public long BeginBatch(SqliteConnection c, SqliteTransaction t, string note)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO op_journal(state,note,at) VALUES('prepared',$n,$a)";
        cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return LastRowId(c, t);
    }

    public void CompleteBatch(SqliteConnection c, SqliteTransaction t, long batchId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "UPDATE op_journal SET state='committed' WHERE batch_id=$b";
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.ExecuteNonQuery();
    }

    private static long LastRowId(SqliteConnection c, SqliteTransaction t)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public ChronoEvent AppendEvent(ChronoEvent e, IEnumerable<DerivedModel>? snapshots = null,
        string actor = "user", string? freezeModelId = null, string? freezeHolderActor = null)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        var batch = BeginBatch(c, t, "append:" + e.Type);

        // 并发冻结：锁已被其他持有者占据且尚未解决 → 冲突事件，冻结不生效
        if (freezeModelId is not null)
        {
            var holder = LockHolder(c, t, freezeModelId);
            if (holder is not null && holder.HolderActor != freezeHolderActor)
                throw new ConcurrentFreezeException(freezeModelId, holder.HolderActor);
            using var lk = c.CreateCommand();
            lk.Transaction = t;
            lk.CommandText = "INSERT OR IGNORE INTO freeze_locks(model_id,holder_actor,held_since_event_id,at)" +
                             " VALUES($m,$h,$e,$a)";
            lk.Parameters.AddWithValue("$m", freezeModelId);
            lk.Parameters.AddWithValue("$h", freezeHolderActor ?? actor);
            lk.Parameters.AddWithValue("$e", e.Id);
            lk.Parameters.AddWithValue("$a", e.At);
            lk.ExecuteNonQuery();
        }

        long seq = NextSeq(c, t, e.BranchId);
        using var ins = c.CreateCommand();
        ins.Transaction = t;
        ins.CommandText = """
        INSERT INTO events(seq,branch_id,event_id,type,at,actor,payload_json,parents_json,
          input_digest,parameter_version,derived_model_id,adopted)
        VALUES($seq,$br,$id,$ty,$at,$ac,$pj,$pa,$dig,$pv,$dm,$ad)
        """;
        ins.Parameters.AddWithValue("$seq", seq);
        ins.Parameters.AddWithValue("$br", e.BranchId);
        ins.Parameters.AddWithValue("$id", e.Id);
        ins.Parameters.AddWithValue("$ty", e.Type);
        ins.Parameters.AddWithValue("$at", e.At);
        ins.Parameters.AddWithValue("$ac", (object?)e.Actor ?? DBNull.Value);
        ins.Parameters.AddWithValue("$pj", e.Payload.ToJsonString());
        ins.Parameters.AddWithValue("$pa", JsonSerializer.Serialize(e.Parents));
        ins.Parameters.AddWithValue("$dig", (object?)e.InputDigest ?? DBNull.Value);
        ins.Parameters.AddWithValue("$pv", (object?)e.ParameterVersion ?? DBNull.Value);
        ins.Parameters.AddWithValue("$dm", (object?)e.DerivedModelId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$ad", e.Adopted ? 1 : 0);
        ins.ExecuteNonQuery();

        if (snapshots is not null)
        {
            foreach (var m in snapshots)
            {
                using var sm = c.CreateCommand();
                sm.Transaction = t;
                sm.CommandText = """
                INSERT INTO derived_models(model_id,branch_id,model_json,input_digest,parameter_version,created_seq,adopted)
                VALUES($id,$br,$j,$dig,$pv,$seq,$ad)
                ON CONFLICT(model_id,branch_id) DO NOTHING
                """;
                sm.Parameters.AddWithValue("$id", m.Id);
                sm.Parameters.AddWithValue("$br", e.BranchId);
                sm.Parameters.AddWithValue("$j", JsonSerializer.Serialize(m, Jo));
                sm.Parameters.AddWithValue("$dig", m.InputDigest);
                sm.Parameters.AddWithValue("$pv", m.ParameterVersion);
                sm.Parameters.AddWithValue("$seq", seq);
                sm.Parameters.AddWithValue("$ad", m.SemanticallyValid ? 1 : 0);
                sm.ExecuteNonQuery();
            }
        }
        CompleteBatch(c, t, batch);
        t.Commit();
        return e with { Seq = seq };
    }

    public sealed record FreezeLockInfo(string ModelId, string HolderActor, string HeldSinceEventId, string At);

    public FreezeLockInfo? LockHolder(SqliteConnection c, SqliteTransaction t, string modelId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "SELECT model_id,holder_actor,held_since_event_id,at FROM freeze_locks WHERE model_id=$m";
        cmd.Parameters.AddWithValue("$m", modelId);
        using var rd = cmd.ExecuteReader();
        return rd.Read()
            ? new FreezeLockInfo(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3))
            : null;
    }

    private static long NextSeq(SqliteConnection c, SqliteTransaction t, string branch)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events WHERE branch_id=$b";
        cmd.Parameters.AddWithValue("$b", branch);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---- 读取 ----------------------------------------------------------------

    public IReadOnlyList<ChronoEvent> ReadEvents(string branchId)
    {
        using var c = Open();
        return ReadEvents(c, null, branchId);
    }

    public IReadOnlyList<ChronoEvent> ReadAllEvents()
    {
        using var c = Open();
        return ReadEvents(c, null, null);
    }

    private static IReadOnlyList<ChronoEvent> ReadEvents(SqliteConnection c, SqliteTransaction? t, string? branchId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,branch_id,event_id,type,at,actor,payload_json,parents_json," +
                          "input_digest,parameter_version,derived_model_id,adopted FROM events" +
                          (branchId is null ? "" : " WHERE branch_id=$b") + " ORDER BY seq";
        if (branchId is not null) cmd.Parameters.AddWithValue("$b", branchId);
        using var rd = cmd.ExecuteReader();
        var list = new List<ChronoEvent>();
        while (rd.Read())
        {
            list.Add(new ChronoEvent(
                rd.GetInt64(0), rd.GetString(2), rd.GetString(1), rd.GetString(3), rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                JsonNodeSafe(rd.GetString(6)),
                JsonSerializer.Deserialize<List<string>>(rd.GetString(7)) ?? new List<string>(),
                rd.IsDBNull(8) ? "" : rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetString(9),
                rd.IsDBNull(10) ? null : rd.GetString(10),
                rd.GetInt32(11) == 1));
        }
        return list;
    }

    private static System.Text.Json.Nodes.JsonNode JsonNodeSafe(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)!; }
        catch
        {
            throw new FormatCorruptedImportException("事件负载不是合法 JSON", json);
        }
    }

    public IReadOnlyList<DerivedModel> ReadDerivedModels(string branchId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT model_json FROM derived_models WHERE branch_id=$b";
        cmd.Parameters.AddWithValue("$b", branchId);
        using var rd = cmd.ExecuteReader();
        var list = new List<DerivedModel>();
        while (rd.Read())
            list.Add(JsonSerializer.Deserialize<DerivedModel>(rd.GetString(0), Jo)!);
        return list;
    }

    public IReadOnlyList<BranchRow> ListBranches()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT branch_id,parent_branch_id,from_seq,baseline_digest,changed_source_id,purpose " +
                          "FROM branches ORDER BY CASE branch_id WHEN 'main' THEN 0 ELSE 1 END, branch_id";
        using var rd = cmd.ExecuteReader();
        var list = new List<BranchRow>();
        while (rd.Read())
            list.Add(new BranchRow(rd.GetString(0),
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? 0 : rd.GetInt64(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? "" : rd.GetString(5)));
        return list;
    }

    public record BranchRow(string BranchId, string? ParentBranchId, long FromSeq,
        string? BaselineDigest, string? ChangedSourceId, string Purpose);

    public void ForkBranch(string newBranchId, string parentBranchId, long fromSeq,
        string projectId, string? baselineDigest, string? changedSourceId, string purpose)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        var batch = BeginBatch(c, t, "fork:" + newBranchId);
        using var ins = c.CreateCommand();
        ins.Transaction = t;
        ins.CommandText = """
        INSERT INTO branches(branch_id,project_id,parent_branch_id,from_seq,baseline_digest,changed_source_id,purpose)
        VALUES($b,$p,$par,$fs,$bd,$cs,$pu)
        """;
        ins.Parameters.AddWithValue("$b", newBranchId);
        ins.Parameters.AddWithValue("$p", projectId);
        ins.Parameters.AddWithValue("$par", (object?)parentBranchId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$fs", fromSeq);
        ins.Parameters.AddWithValue("$bd", (object?)baselineDigest ?? DBNull.Value);
        ins.Parameters.AddWithValue("$cs", (object?)changedSourceId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$pu", purpose);
        ins.ExecuteNonQuery();
        CompleteBatch(c, t, batch);
        t.Commit();
    }
}
