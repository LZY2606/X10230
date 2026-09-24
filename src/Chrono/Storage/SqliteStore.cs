using Chrono.Domain;
using Microsoft.Data.Sqlite;

namespace Chrono.Storage;

public interface IEventStore : IDisposable
{
    void Init();
    LedgerEvent Append(LedgerEvent e, string? expectedLastHash);
    IReadOnlyList<LedgerEvent> LoadBranch(string branchId);
    IReadOnlyList<string> ListBranches();
    void DeleteAll();
    string ExportJson();
    /// <summary>导入档案：身份、顺序、来源关系保持；返回导入事件数。</summary>
    int ImportJson(string json, out List<Diagnostic> diagnostics);
    string? LastHash(string branchId);
    void AssertChainIntegrity();
}

/// <summary>
/// SQLite 事件账本。所有写操作在单个事务内提交：进程在事务提交前被杀死 →
/// 下次打开自动回滚（半写恢复）。
/// </summary>
public sealed class SqliteStore : IEventStore
{
    private readonly string _connectionString;

    public SqliteStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS branches(
          branch_id TEXT PRIMARY KEY,
          parent_branch_id TEXT,
          name TEXT NOT NULL,
          created_event_id TEXT,
          created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS events(
          seq INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL UNIQUE,
          branch_id TEXT NOT NULL,
          type TEXT NOT NULL,
          actor TEXT NOT NULL,
          occurred_at TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          input_digest TEXT NOT NULL,
          parameter_version TEXT NOT NULL,
          parent_event_id TEXT NOT NULL,
          prev_hash TEXT NOT NULL,
          hash TEXT NOT NULL UNIQUE,
          outcome TEXT NOT NULL,
          diagnostics_json TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_events_branch ON events(branch_id, seq);
        """;
        cmd.ExecuteNonQuery();
    }

    public LedgerEvent Append(LedgerEvent e, string? expectedLastHash)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var current = LastHash(conn, e.BranchId);
        if (expectedLastHash is not null && current != expectedLastHash)
            throw new ConcurrencyConflictException(
                $"并发修改：分支 {e.BranchId} 已从 {expectedLastHash[..8]} 变为 {current?[..8]}");

        if (e.Seq == 0)
        {
            using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(seq)+1,1) FROM events";
            e.Seq = (long)(seqCmd.ExecuteScalar() ?? 1L);
        }
        e.PrevHash = current ?? "";
        e.Hash = ComputeHash(e);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
        INSERT INTO events(event_id,branch_id,type,actor,occurred_at,payload_json,
          input_digest,parameter_version,parent_event_id,prev_hash,hash,outcome,diagnostics_json)
        VALUES($id,$br,$type,$actor,$at,$payload,$digest,$pv,$parent,$prev,$hash,$outcome,$diag)
        """;
        Bind(cmd, e);
        cmd.ExecuteNonQuery();

        using var brCmd = conn.CreateCommand();
        brCmd.Transaction = tx;
        brCmd.CommandText = "INSERT OR IGNORE INTO branches(branch_id,name,created_at) VALUES($b,$n,$a)";
        brCmd.Parameters.AddWithValue("$b", e.BranchId);
        brCmd.Parameters.AddWithValue("$n", e.BranchId);
        brCmd.Parameters.AddWithValue("$a", e.OccurredAt);
        brCmd.ExecuteNonQuery();

        tx.Commit();
        return e;
    }

    private static void Bind(SqliteCommand cmd, LedgerEvent e)
    {
        cmd.Parameters.AddWithValue("$id", e.EventId);
        cmd.Parameters.AddWithValue("$br", e.BranchId);
        cmd.Parameters.AddWithValue("$type", e.Type.ToString());
        cmd.Parameters.AddWithValue("$actor", e.Actor);
        cmd.Parameters.AddWithValue("$at", e.OccurredAt);
        cmd.Parameters.AddWithValue("$payload", e.PayloadJson);
        cmd.Parameters.AddWithValue("$digest", e.InputDigest);
        cmd.Parameters.AddWithValue("$pv", e.ParameterVersion);
        cmd.Parameters.AddWithValue("$parent", e.ParentEventId);
        cmd.Parameters.AddWithValue("$prev", e.PrevHash);
        cmd.Parameters.AddWithValue("$hash", e.Hash);
        cmd.Parameters.AddWithValue("$outcome", e.Outcome);
        cmd.Parameters.AddWithValue("$diag", (object?)e.DiagnosticsJson ?? DBNull.Value);
    }

    public IReadOnlyList<LedgerEvent> LoadBranch(string branchId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM events WHERE branch_id=$b ORDER BY seq";
        cmd.Parameters.AddWithValue("$b", branchId);
        return Read(cmd);
    }

    public IReadOnlyList<string> ListBranches()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT branch_id FROM branches ORDER BY branch_id";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void DeleteAll()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "events", "branches" })
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = $"DELETE FROM {table}";
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public string? LastHash(string branchId)
    {
        using var conn = Open();
        return LastHash(conn, branchId);
    }

    private static string? LastHash(SqliteConnection conn, string branchId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash FROM events WHERE branch_id=$b ORDER BY seq DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$b", branchId);
        return cmd.ExecuteScalar() as string;
    }

    private static List<LedgerEvent> Read(SqliteCommand cmd)
    {
        var list = new List<LedgerEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LedgerEvent
            {
                Seq = r.GetInt64(r.GetOrdinal("seq")),
                EventId = r.GetString(r.GetOrdinal("event_id")),
                BranchId = r.GetString(r.GetOrdinal("branch_id")),
                Type = Enum.Parse<EventType>(r.GetString(r.GetOrdinal("type"))),
                Actor = r.GetString(r.GetOrdinal("actor")),
                OccurredAt = r.GetString(r.GetOrdinal("occurred_at")),
                PayloadJson = r.GetString(r.GetOrdinal("payload_json")),
                InputDigest = r.GetString(r.GetOrdinal("input_digest")),
                ParameterVersion = r.GetString(r.GetOrdinal("parameter_version")),
                ParentEventId = r.GetString(r.GetOrdinal("parent_event_id")),
                PrevHash = r.GetString(r.GetOrdinal("prev_hash")),
                Hash = r.GetString(r.GetOrdinal("hash")),
                Outcome = r.GetString(r.GetOrdinal("outcome")),
                DiagnosticsJson = r.IsDBNull(r.GetOrdinal("diagnostics_json")) ? null : r.GetString(r.GetOrdinal("diagnostics_json")),
            });
        }
        return list;
    }

    public static string ComputeHash(LedgerEvent e)
    {
        var material = string.Join("\n", new[]
        {
            e.Seq.ToString(), e.EventId, e.BranchId, e.Type.ToString(), e.Actor, e.OccurredAt,
            e.PayloadJson, e.InputDigest, e.ParameterVersion, e.ParentEventId, e.PrevHash, e.Outcome,
        });
        return Canonical.DigestText(material);
    }

    public void AssertChainIntegrity()
    {
        foreach (var branch in ListBranches())
        {
            var events = LoadBranch(branch);
            string prev = "";
            foreach (var ev in events)
            {
                if (ev.PrevHash != prev)
                    throw new ChainBrokenException($"分支 {branch} 事件 {ev.EventId} 前序哈希不匹配（半写/篡改）");
                if (ComputeHash(ev) != ev.Hash)
                    throw new ChainBrokenException($"分支 {branch} 事件 {ev.EventId} 内容哈希不匹配");
                prev = ev.Hash;
            }
        }
    }

    public string ExportJson()
    {
        AssertChainIntegrity();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM events ORDER BY branch_id, seq";
        var rows = Read(cmd);
        var archive = new
        {
            archiveSchema = "chrono-ledger-export/1",
            exportedAt = DateTimeOffset.UtcNow.ToString("O"),
            events = rows,
        };
        return Canonical.Serialize(archive);
    }

    public int ImportJson(string json, out List<Diagnostic> diagnostics)
    {
        diagnostics = new List<Diagnostic>();
        JsonArchive? archive;
        try
        {
            archive = System.Text.Json.JsonSerializer.Deserialize<JsonArchive>(json, Canonical.Json);
            if (archive is null || archive.Events is null)
                throw new InvalidDataException("档案为空");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidDataException)
        {
            throw new CaseFormatException($"导出档案格式损坏：{ex.Message}");
        }

        var count = 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var seen = new HashSet<string>();
        foreach (var e in archive.Events)
        {
            if (!seen.Add(e.EventId))
                throw new CaseFormatException($"档案内事件身份重复：{e.EventId}");
            if (ComputeHash(e) != e.Hash || !string.IsNullOrEmpty(e.PrevHash) == false && false) { /* checked below */ }
            if (ComputeHash(e) != e.Hash)
                throw new CaseFormatException($"档案事件 {e.EventId} 哈希不一致，拒绝导入");

            using var exists = conn.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = "SELECT hash FROM events WHERE event_id=$id";
            exists.Parameters.AddWithValue("$id", e.EventId);
            var existing = exists.ExecuteScalar() as string;
            if (existing is not null)
            weird:;
            if (existing is not null)
            {
                if (existing != e.Hash)
                    throw new CaseFormatException($"事件 {e.EventId} 身份相同但内容不同，身份关系被破坏");
                continue;
            }

            using var prev = conn.CreateCommand();
            prev.Transaction = tx;
            prev.CommandText = "SELECT hash FROM events WHERE branch_id=$b AND event_id=$p";
            prev.Parameters.AddWithValue("$b", e.BranchId);
            prev.Parameters.AddWithValue("$p", e.ParentEventId);
            var parentExists = string.IsNullOrEmpty(e.ParentEventId) || prev.ExecuteScalar() is not null;
            if (!parentExists)
                throw new CaseFormatException($"事件 {e.EventId} 来源关系断裂：父事件 {e.ParentEventId} 缺失");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO events(event_id,branch_id,type,actor,occurred_at,payload_json,
              input_digest,parameter_version,parent_event_id,prev_hash,hash,outcome,diagnostics_json)
            VALUES($id,$br,$type,$actor,$at,$payload,$digest,$pv,$parent,$prev,$hash,$outcome,$diag)
            """;
            Bind(cmd, e);
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        return count;
    }

    public void Dispose() { }

    private sealed class JsonArchive
    {
        public string? ArchiveSchema { get; set; }
        public List<LedgerEvent>? Events { get; set; }
    }
}

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}

public sealed class ChainBrokenException : Exception
{
    public ChainBrokenException(string message) : base(message) { }
}
