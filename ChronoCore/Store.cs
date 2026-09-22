using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoCore;

public sealed class SqliteStore
{
    private readonly string _connectionString;
    public string DbPath { get; }

    public SqliteStore(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS events (
            seq INTEGER PRIMARY KEY,
            type TEXT NOT NULL,
            at TEXT NOT NULL,
            actor TEXT NOT NULL,
            rule_version TEXT NOT NULL,
            input_summary TEXT NOT NULL,
            parent_hashes TEXT NOT NULL,
            payload TEXT NOT NULL,
            hash TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS documents (
            doc_type TEXT NOT NULL,
            doc_id TEXT NOT NULL,
            rev INTEGER NOT NULL,
            seq INTEGER NOT NULL,
            json TEXT NOT NULL,
            PRIMARY KEY (doc_type, doc_id, rev)
        );
        CREATE TABLE IF NOT EXISTS import_ops (
            op_id TEXT PRIMARY KEY,
            total_chunks INTEGER NOT NULL,
            received INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS import_chunks (
            op_id TEXT NOT NULL,
            idx INTEGER NOT NULL,
            body TEXT NOT NULL,
            PRIMARY KEY (op_id, idx)
        );
        CREATE TABLE IF NOT EXISTS freeze_locks (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            candidate_id TEXT NOT NULL,
            actor TEXT NOT NULL,
            seq INTEGER NOT NULL,
            at TEXT NOT NULL
        );
";
        cmd.ExecuteNonQuery();
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public List<ChronoEvent> LoadEvents()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq,type,at,actor,rule_version,input_summary,parent_hashes,payload,hash FROM events ORDER BY seq";
        using var r = cmd.ExecuteReader();
        var list = new List<ChronoEvent>();
        while (r.Read())
            list.Add(new ChronoEvent
            {
                Seq = r.GetInt64(0), Type = r.GetString(1), At = r.GetString(2),
                Actor = r.GetString(3), RuleVersion = r.GetString(4),
                InputSummary = r.GetString(5), ParentHashes = r.GetString(6),
                Payload = JsonSerializer.Deserialize<JsonElement>(r.GetString(7)),
                Hash = r.GetString(8)
            });
        return list;
    }

    /// <summary>Append an event inside an existing transaction; hash chains to the previous event.</summary>
    public ChronoEvent AppendEvent(SqliteConnection conn, SqliteTransaction tx,
        string type, object payload, string actor = "system",
        string inputSummary = "", string parentHashes = "")
    {
        long seq;
        string prevHash;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(seq),0), COALESCE((SELECT hash FROM events ORDER BY seq DESC LIMIT 1),'') ";
            using var rr = q.ExecuteReader();
            rr.Read();
            seq = rr.GetInt64(0) + 1;
            prevHash = rr.GetString(1);
        }
        string payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        var ev = new ChronoEvent
        {
            Seq = seq, Type = type, At = DateTimeOffset.UtcNow.ToString("o"),
            Actor = actor, RuleVersion = RuleSet.Version,
            InputSummary = inputSummary, ParentHashes = parentHashes,
            Payload = JsonSerializer.Deserialize<JsonElement>(payloadJson),
        };
        ev.Hash = Sha256($"{prevHash}|{seq}|{type}|{payloadJson}");
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO events
                (seq,type,at,actor,rule_version,input_summary,parent_hashes,payload,hash)
                VALUES ($s,$t,$a,$ac,$rv,$is,$ph,$p,$h)
";
            ins.Parameters.AddWithValue("$s", ev.Seq);
            ins.Parameters.AddWithValue("$t", ev.Type);
            ins.Parameters.AddWithValue("$a", ev.At);
            ins.Parameters.AddWithValue("$ac", ev.Actor);
            ins.Parameters.AddWithValue("$rv", ev.RuleVersion);
            ins.Parameters.AddWithValue("$is", ev.InputSummary);
            ins.Parameters.AddWithValue("$ph", ev.ParentHashes);
            ins.Parameters.AddWithValue("$p", payloadJson);
            ins.Parameters.AddWithValue("$h", ev.Hash);
            ins.ExecuteNonQuery();
        }
        return ev;
    }

    public SqliteConnection Begin(out SqliteTransaction tx)
    {
        var conn = Open();
        tx = conn.BeginTransaction();
        return conn;
    }

    public void PutDocument(SqliteConnection conn, SqliteTransaction tx,
        string docType, string docId, int rev, long seq, string json)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO documents(doc_type,doc_id,rev,seq,json)
            VALUES($t,$i,$r,$s,$j)
            ON CONFLICT(doc_type,doc_id,rev) DO UPDATE SET seq=$s,json=$j
";
        cmd.Parameters.AddWithValue("$t", docType);
        cmd.Parameters.AddWithValue("$i", docId);
        cmd.Parameters.AddWithValue("$r", rev);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    public List<(string docId, int rev, long seq, string json)> GetDocumentHistory(
        SqliteConnection conn, SqliteTransaction tx, string docType, string docId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT doc_id,rev,seq,json FROM documents WHERE doc_type=$t AND doc_id=$i ORDER BY rev";
        cmd.Parameters.AddWithValue("$t", docType);
        cmd.Parameters.AddWithValue("$i", docId);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, long, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetString(3)));
        return list;
    }

    public List<(string docType, string docId, int rev, long seq, string json)> GetAllDocuments()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT doc_type,doc_id,rev,seq,json FROM documents ORDER BY doc_type,doc_id,rev";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, int, long, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4)));
        return list;
    }

    public string GetLatestDocument(string docType, string docId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE doc_type=$t AND doc_id=$i ORDER BY rev DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$t", docType);
        cmd.Parameters.AddWithValue("$i", docId);
        return cmd.ExecuteScalar() as string;
    }

    // ---------------- chunked import staging (half-write recovery) ----------------

    public void BeginImportOp(string opId, int totalChunks)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO import_ops(op_id,total_chunks,received,created_at)
            VALUES($i,$t,0,$c)
            ON CONFLICT(op_id) DO NOTHING
";
        cmd.Parameters.AddWithValue("$i", opId);
        cmd.Parameters.AddWithValue("$t", totalChunks);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persist one chunk independently; a crash leaves a resumable, detectable half-write.</summary>
    public void WriteImportChunk(string opId, int idx, string body)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO import_chunks(op_id,idx,body) VALUES($o,$i,$b)
                ON CONFLICT(op_id,idx) DO UPDATE SET body=$b
";
            cmd.Parameters.AddWithValue("$o", opId);
            cmd.Parameters.AddWithValue("$i", idx);
            cmd.Parameters.AddWithValue("$b", body);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE import_ops SET received = (SELECT COUNT(*) FROM import_chunks WHERE op_id=$o) WHERE op_id=$o";
            cmd.Parameters.AddWithValue("$o", opId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public (int total, int received, List<(int idx, string body)> chunks) GetImportProgress(string opId)
    {
        using var conn = Open();
        int total, received;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT total_chunks,received FROM import_ops WHERE op_id=$o";
            cmd.Parameters.AddWithValue("$o", opId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0, new());
            total = r.GetInt32(0); received = r.GetInt32(1);
        }
        var chunks = new List<(int, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT idx,body FROM import_chunks WHERE op_id=$o ORDER BY idx";
            cmd.Parameters.AddWithValue("$o", opId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) chunks.Add((r.GetInt32(0), r.GetString(1)));
        }
        return (total, received, chunks);
    }

    public void DeleteImportOp(string opId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "import_chunks", "import_ops" })
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE op_id=$o";
                cmd.Parameters.AddWithValue("$o", opId);
                cmd.ExecuteNonQuery();
            }
        tx.Commit();
    }

    // ---------------- freeze lock (optimistic concurrency) ----------------

    public sealed class FreezeOutcome
    {
        public bool Ok;
        public string CandidateId;
        public long Seq;
        public string At;
        public string Actor;
        public string ConflictWith;
    }

    public FreezeOutcome TryFreeze(string candidateId, string actor,
        object payload, string inputSummary, string parentHashes)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        FreezeOutcome outcome;
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT candidate_id,seq,actor FROM freeze_locks WHERE id=1";
            using var r = check.ExecuteReader();
            if (r.Read())
            {
                outcome = new FreezeOutcome
                {
                    Ok = false, CandidateId = r.GetString(0), Seq = r.GetInt64(1), Actor = r.GetString(2),
                    ConflictWith = r.GetString(0)
                };
                r.Close();
                AppendEvent(conn, tx, EventType.FreezeRejected, new
                {
                    requestedCandidateId = candidateId, actor,
                    conflictWith = outcome.ConflictWith, frozenSeq = outcome.Seq
                }, actor, $"freeze conflict: {candidateId} vs {outcome.ConflictWith}");
                tx.Commit();
                return outcome;
            }
        }
        var ev = AppendEvent(conn, tx, EventType.ModelFrozen,
            payload, actor, inputSummary, parentHashes);
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO freeze_locks(id,candidate_id,actor,seq,at) VALUES(1,$c,$a,$s,$t)";
            ins.Parameters.AddWithValue("$c", candidateId);
            ins.Parameters.AddWithValue("$a", actor);
            ins.Parameters.AddWithValue("$s", ev.Seq);
            ins.Parameters.AddWithValue("$t", ev.At);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return new FreezeOutcome { Ok = true, CandidateId = candidateId, Seq = ev.Seq, At = ev.At, Actor = actor };
    }

    public FreezeOutcome CurrentFreeze()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT candidate_id,seq,actor,at FROM freeze_locks WHERE id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new FreezeOutcome { Ok = true, CandidateId = r.GetString(0), Seq = r.GetInt64(1), Actor = r.GetString(2), At = r.GetString(3) };
    }

    public void ClearFreeze(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM freeze_locks WHERE id=1";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replace the whole database from a bundle inside one transaction (used by import).</summary>
    public void ReplaceAll(IEnumerable<ChronoEvent> events,
        IEnumerable<(string docType, string docId, int rev, long seq, string json)> docs,
        FreezeOutcome freeze)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "freeze_locks", "documents", "events", "import_chunks", "import_ops" })
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table}";
                cmd.ExecuteNonQuery();
            }
        foreach (var ev in events.OrderBy(e => e.Seq))
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO events
                    (seq,type,at,actor,rule_version,input_summary,parent_hashes,payload,hash)
                    VALUES ($s,$t,$a,$ac,$rv,$is,$ph,$p,$h)
";
                cmd.Parameters.AddWithValue("$s", ev.Seq);
                cmd.Parameters.AddWithValue("$t", ev.Type);
                cmd.Parameters.AddWithValue("$a", ev.At);
                cmd.Parameters.AddWithValue("$ac", ev.Actor);
                cmd.Parameters.AddWithValue("$rv", ev.RuleVersion);
                cmd.Parameters.AddWithValue("$is", ev.InputSummary);
                cmd.Parameters.AddWithValue("$ph", ev.ParentHashes);
                cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(ev.Payload, JsonOpts));
                cmd.Parameters.AddWithValue("$h", ev.Hash);
                cmd.ExecuteNonQuery();
            }
        foreach (var d in docs)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO documents(doc_type,doc_id,rev,seq,json) VALUES($t,$i,$r,$s,$j)";
                cmd.Parameters.AddWithValue("$t", d.docType);
                cmd.Parameters.AddWithValue("$i", d.docId);
                cmd.Parameters.AddWithValue("$r", d.rev);
                cmd.Parameters.AddWithValue("$s", d.seq);
                cmd.Parameters.AddWithValue("$j", d.json);
                cmd.ExecuteNonQuery();
            }
        if (freeze != null)
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO freeze_locks(id,candidate_id,actor,seq,at) VALUES(1,$c,$a,$s,$t)";
                cmd.Parameters.AddWithValue("$s", freeze.Seq);
                cmd.Parameters.AddWithValue("$c", freeze.CandidateId);
                cmd.Parameters.AddWithValue("$a", freeze.Actor ?? "system");
                cmd.Parameters.AddWithValue("$t", freeze.At ?? "");
                cmd.ExecuteNonQuery();
            }
        tx.Commit();
    }

    public bool VerifyChain(out long brokenAt)
    {
        var events = LoadEvents();
        string prev = "";
        brokenAt = 0;
        foreach (var ev in events)
        {
            string payloadJson = JsonSerializer.Serialize(ev.Payload, JsonOpts);
            string expect = Sha256($"{prev}|{ev.Seq}|{ev.Type}|{payloadJson}");
            if (expect != ev.Hash) { brokenAt = ev.Seq; return false; }
            prev = ev.Hash;
        }
        return true;
    }
}
