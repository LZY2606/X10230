using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Chrono.Core;

/// <summary>
/// SQLite-backed event store. Writes are serialised per project inside a transaction with an
/// optimistic version check, so concurrent freezes/derivations produce one winner and one explicit
/// rejection rather than last-write-wins.
/// </summary>
public sealed class SqliteProjectStore : IProjectStore
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public SqliteProjectStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    public void Initialize()
    {
        lock (_lock)
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS projects(
                  project_id TEXT PRIMARY KEY, name TEXT NOT NULL, case_id TEXT NOT NULL,
                  created_at TEXT NOT NULL, bundle_meta TEXT NULL);
                CREATE TABLE IF NOT EXISTS branches(
                  project_id TEXT NOT NULL, branch_id TEXT NOT NULL,
                  parent_branch_id TEXT NULL, fork_seq INTEGER NOT NULL DEFAULT 0,
                  reason TEXT NOT NULL DEFAULT '',
                  PRIMARY KEY(project_id, branch_id),
                  FOREIGN KEY(project_id) REFERENCES projects(project_id));
                CREATE TABLE IF NOT EXISTS events(
                  project_id TEXT NOT NULL, branch_id TEXT NOT NULL, seq INTEGER NOT NULL,
                  event_id TEXT NOT NULL UNIQUE, author TEXT NOT NULL, created_at TEXT NOT NULL,
                  input_digest TEXT NOT NULL DEFAULT '', parent_event_id TEXT NOT NULL DEFAULT '',
                  event_type TEXT NOT NULL, payload_json TEXT NOT NULL,
                  PRIMARY KEY(project_id, branch_id, seq),
                  FOREIGN KEY(project_id, branch_id) REFERENCES branches(project_id, branch_id));
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public string CreateProject(string projectId, string name, string caseId)
    {
        lock (_lock)
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            using var p = c.CreateCommand();
            p.Transaction = tx;
            p.CommandText = """
                INSERT OR IGNORE INTO projects(project_id,name,case_id,created_at)
                VALUES($id,$name,$caseId,$ts);
                INSERT OR IGNORE INTO branches(project_id,branch_id,parent_branch_id,fork_seq,reason)
                VALUES($id,'main',NULL,0,'mainline');
                """;
            p.Parameters.AddWithValue("$id", projectId);
            p.Parameters.AddWithValue("$name", name);
            p.Parameters.AddWithValue("$caseId", caseId);
            p.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            p.ExecuteNonQuery();
            tx.Commit();
        }
        return projectId;
    }

    public bool ProjectExists(string projectId)
    {
        using var c = Open();
        using var p = c.CreateCommand();
        p.CommandText = "SELECT 1 FROM projects WHERE project_id=$id";
        p.Parameters.AddWithValue("$id", projectId);
        return p.ExecuteScalar() is not null;
    }

    public BundleInfo? LoadBundleInfo(string projectId)
    {
        using var c = Open();
        using var p = c.CreateCommand();
        p.CommandText = "SELECT name,case_id,created_at FROM projects WHERE project_id=$id";
        p.Parameters.AddWithValue("$id", projectId);
        using var r = p.ExecuteReader();
        if (!r.Read()) return null;
        return new BundleInfo(projectId, r.GetString(0), r.GetString(1), r.GetString(2),
            CountEvents(c, projectId));
    }

    private static int CountEvents(SqliteConnection c, string projectId)
    {
        using var p = c.CreateCommand();
        p.CommandText = "SELECT COUNT(*) FROM events WHERE project_id=$id";
        p.Parameters.AddWithValue("$id", projectId);
        return Convert.ToInt32(p.ExecuteScalar());
    }

    public IReadOnlyList<ChronoEvent> ReadStream(string projectId, string branchId)
    {
        var list = new List<ChronoEvent>();
        using var c = Open();
        using var p = c.CreateCommand();
        p.CommandText = """
            SELECT event_id,seq,author,created_at,input_digest,parent_event_id,event_type,payload_json
            FROM events WHERE project_id=$pid AND branch_id=$bid ORDER BY seq ASC;
            """;
        p.Parameters.AddWithValue("$pid", projectId);
        p.Parameters.AddWithValue("$bid", branchId);
        using var r = p.ExecuteReader();
        while (r.Read())
        {
            var payload = JsonSerializer.Deserialize<EventPayload>(r.GetString(7), JsonOpts)
                          ?? throw new InvalidDataException("null payload in store");
            list.Add(new ChronoEvent
            {
                ProjectId = projectId, BranchId = branchId, EventId = r.GetString(0),
                Seq = r.GetInt32(1), Author = r.GetString(2), CreatedAtUtc = r.GetString(3),
                InputDigest = r.GetString(4), ParentEventId = r.GetString(5),
                EventType = r.GetString(6), Payload = payload
            });
        }
        return list;
    }

    public ChronoEvent? TryAppend(string projectId, string branchId, int expectedVersion,
        EventPayload payload, string author, string inputDigest, string? parentEventId, string createdAtUtc)
    {
        lock (_lock)
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            int current;
            string? lastEventId;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COUNT(*), COALESCE(MAX(event_id),'') FROM events " +
                                "WHERE project_id=$pid AND branch_id=$bid";
                q.Parameters.AddWithValue("$pid", projectId);
                q.Parameters.AddWithValue("$bid", branchId);
                using var rr = q.ExecuteReader();
                rr.Read();
                current = rr.GetInt32(0);
                lastEventId = rr.GetString(1);
            }
            if (current != expectedVersion)
            {
                tx.Rollback();
                return null;
            }

            var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
            var seq = current;
            var eventId = ModelingEngine.StableId("evt",
                $"{projectId}|{branchId}|{seq}|{payloadJson}|{createdAtUtc}");

            using (var w = c.CreateCommand())
            {
                w.Transaction = tx;
                w.CommandText = """
                    INSERT INTO events(project_id,branch_id,seq,event_id,author,created_at,
                                       input_digest,parent_event_id,event_type,payload_json)
                    VALUES($pid,$bid,$seq,$eid,$au,$ts,$dig,$par,$et,$pj);
                    """;
                w.Parameters.AddWithValue("$pid", projectId);
                w.Parameters.AddWithValue("$bid", branchId);
                w.Parameters.AddWithValue("$seq", seq);
                w.Parameters.AddWithValue("$eid", eventId);
                w.Parameters.AddWithValue("$au", author);
                w.Parameters.AddWithValue("$ts", createdAtUtc);
                w.Parameters.AddWithValue("$dig", inputDigest ?? "");
                w.Parameters.AddWithValue("$par", parentEventId ?? lastEventId ?? "");
                w.Parameters.AddWithValue("$et", payload.GetType().Name);
                w.Parameters.AddWithValue("$pj", payloadJson);
                w.ExecuteNonQuery();
            }
            tx.Commit();
            return new ChronoEvent
            {
                EventId = eventId, ProjectId = projectId, BranchId = branchId, Seq = seq,
                Author = author, CreatedAtUtc = createdAtUtc, InputDigest = inputDigest ?? "",
                ParentEventId = parentEventId ?? lastEventId ?? "",
                EventType = payload.GetType().Name, Payload = payload
            };
        }
    }

    public IReadOnlyList<BranchInfo> ListBranches(string projectId)
    {
        var list = new List<BranchInfo>();
        using var c = Open();
        using var p = c.CreateCommand();
        p.CommandText = """
            SELECT b.branch_id,b.parent_branch_id,b.fork_seq,b.reason,
                   (SELECT COUNT(*) FROM events e WHERE e.project_id=b.project_id AND e.branch_id=b.branch_id)
            FROM branches b WHERE b.project_id=$id ORDER BY b.branch_id;
            """;
        p.Parameters.AddWithValue("$id", projectId);
        using var r = p.ExecuteReader();
        while (r.Read())
            list.Add(new BranchInfo(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetInt32(2), r.GetString(3), r.GetInt32(4)));
        return list;
    }

    public string CreateBranch(string projectId, string newBranchId, string fromBranchId, int forkSeq, string reason)
    {
        lock (_lock)
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            using (var p = c.CreateCommand())
            {
                p.Transaction = tx;
                p.CommandText = """
                    INSERT INTO branches(project_id,branch_id,parent_branch_id,fork_seq,reason)
                    VALUES($pid,$nid,$fid,$seq,$reason);
                    """;
                p.Parameters.AddWithValue("$pid", projectId);
                p.Parameters.AddWithValue("$nid", newBranchId);
                p.Parameters.AddWithValue("$fid", fromBranchId);
                p.Parameters.AddWithValue("$seq", forkSeq);
                p.Parameters.AddWithValue("$reason", reason);
                p.ExecuteNonQuery();
            }
            using (var cp = c.CreateCommand())
            {
                cp.Transaction = tx;
                cp.CommandText = """
                    INSERT INTO events(project_id,branch_id,seq,event_id,author,created_at,
                                       input_digest,parent_event_id,event_type,payload_json)
                    SELECT project_id,$nid,seq,event_id,author,created_at,input_digest,parent_event_id,event_type,payload_json
                    FROM events WHERE project_id=$pid AND branch_id=$fid AND seq < $seq;
                    """;
                cp.Parameters.AddWithValue("$pid", projectId);
                cp.Parameters.AddWithValue("$nid", newBranchId);
                cp.Parameters.AddWithValue("$fid", fromBranchId);
                cp.Parameters.AddWithValue("$seq", forkSeq);
                cp.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return newBranchId;
    }

    public void ExportBundle(string projectId, Stream destination)
    {
        using var c = Open();
        var info = LoadBundleInfo(projectId) ?? throw new InvalidOperationException("project missing");
        var branches = new List<object>();
        foreach (var b in ListBranches(projectId))
        {
            var evs = ReadStream(projectId, b.BranchId);
            branches.Add(new
            {
                branchId = b.BranchId,
                parentBranchId = b.ParentBranchId,
                forkSeq = b.ForkSeq,
                reason = b.Reason,
                events = evs.Select(e => new
                {
                    eventId = e.EventId,
                    seq = e.Seq,
                    author = e.Author,
                    createdAtUtc = e.CreatedAtUtc,
                    inputDigest = e.InputDigest,
                    parentEventId = e.ParentEventId,
                    eventType = e.EventType,
                    payload = JsonSerializer.Deserialize<JsonElement>(
                        JsonSerializer.Serialize(e.Payload, JsonOpts), JsonOpts)
                }).ToArray()
            });
        }
        var bundle = new
        {
            format = "chrono-bundle/v1",
            projectId = info.ProjectId,
            name = info.ProjectName,
            caseId = info.CaseId,
            exportedAtUtc = DateTime.UtcNow.ToString("o"),
            eventCount = info.EventCount,
            branches
        };
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, bundle, JsonOpts);
    }

    public (string projectId, bool replaced) ImportBundle(Stream source, bool overwrite = false,
        int? simulateCrashAfterEvents = null)
    {
        BundleFile? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<BundleFile>(source, JsonOpts);
            if (bundle?.Format != "chrono-bundle/v1" ||
                string.IsNullOrEmpty(bundle.ProjectId) || bundle.Branches is null)
                throw new JsonException("missing format/projectId/branches");
        }
        catch (JsonException ex)
        {
            throw new BadBundleException($"bundle is structurally corrupt: {ex.Message}");
        }

        lock (_lock)
        using (var c = Open())
        {
            var exists = ProjectExists(bundle.ProjectId);
            if (exists && !overwrite)
                throw new InvalidOperationException($"project {bundle.ProjectId} already exists");

            using var tx = c.BeginTransaction();
            try
            {
                if (exists)
                {
                    Exec(c, tx, "DELETE FROM events WHERE project_id=$id", bundle.ProjectId);
                    Exec(c, tx, "DELETE FROM branches WHERE project_id=$id", bundle.ProjectId);
                    Exec(c, tx, "DELETE FROM projects WHERE project_id=$id", bundle.ProjectId);
                }
                using (var p = c.CreateCommand())
                {
                    p.Transaction = tx;
                    p.CommandText = "INSERT INTO projects(project_id,name,case_id,created_at) VALUES($id,$n,$k,$ts)";
                    p.Parameters.AddWithValue("$id", bundle.ProjectId);
                    p.Parameters.AddWithValue("$n", bundle.Name ?? "");
                    p.Parameters.AddWithValue("$k", bundle.CaseId ?? "");
                    p.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                    p.ExecuteNonQuery();
                }

                var written = 0;
                foreach (var b in bundle.Branches)
                {
                    using (var br = c.CreateCommand())
                    {
                        br.Transaction = tx;
                        br.CommandText = "INSERT INTO branches(project_id,branch_id,parent_branch_id,fork_seq,reason) " +
                                        "VALUES($pid,$bid,$par,$fk,$rs)";
                        br.Parameters.AddWithValue("$pid", bundle.ProjectId);
                        br.Parameters.AddWithValue("$bid", b.BranchId);
                        br.Parameters.AddWithValue("$par", (object?)b.ParentBranchId ?? DBNull.Value);
                        br.Parameters.AddWithValue("$fk", b.ForkSeq);
                        br.Parameters.AddWithValue("$rs", b.Reason ?? "");
                        br.ExecuteNonQuery();
                    }
                    foreach (var ev in b.Events)
                    {
                        var payload = ev.Payload.Deserialize<EventPayload>(JsonOpts)
                            ?? throw new BadBundleException("event payload undiscriminatable");
                        using var ins = c.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText = """
                            INSERT INTO events(project_id,branch_id,seq,event_id,author,created_at,
                                               input_digest,parent_event_id,event_type,payload_json)
                            VALUES($pid,$bid,$seq,$eid,$au,$ts,$dig,$par,$et,$pj)
                            """;
                        ins.Parameters.AddWithValue("$pid", bundle.ProjectId);
                        ins.Parameters.AddWithValue("$bid", b.BranchId);
                        ins.Parameters.AddWithValue("$seq", ev.Seq);
                        ins.Parameters.AddWithValue("$eid", ev.EventId);
                        ins.Parameters.AddWithValue("$au", ev.Author ?? "reviewer");
                        ins.Parameters.AddWithValue("$ts", ev.CreatedAtUtc);
                        ins.Parameters.AddWithValue("$dig", ev.InputDigest ?? "");
                        ins.Parameters.AddWithValue("$par", ev.ParentEventId ?? "");
                        ins.Parameters.AddWithValue("$et", ev.EventType ?? payload.GetType().Name);
                        ins.Parameters.AddWithValue("$pj", JsonSerializer.Serialize(payload, JsonOpts));
                        ins.ExecuteNonQuery();
                        written++;
                        if (simulateCrashAfterEvents is int crashAt && written >= crashAt)
                            throw new CrashSimulationException();
                    }
                }
                tx.Commit();
            }
            catch (CrashSimulationException)
            {
                tx.Rollback();
                throw;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            return (bundle.ProjectId, exists);
        }
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, string id)
    {
        using var p = c.CreateCommand();
        p.Transaction = tx;
        p.CommandText = sql;
        p.Parameters.AddWithValue("$id", id);
        p.ExecuteNonQuery();
    }

    public void DeleteProject(string projectId)
    {
        lock (_lock)
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            Exec(c, tx, "DELETE FROM events WHERE project_id=$id", projectId);
            Exec(c, tx, "DELETE FROM branches WHERE project_id=$id", projectId);
            Exec(c, tx, "DELETE FROM projects WHERE project_id=$id", projectId);
            tx.Commit();
        }
    }

    public IReadOnlyList<string> ListProjects()
    {
        var list = new List<string>();
        using var c = Open();
        using var p = c.CreateCommand();
        p.CommandText = "SELECT project_id FROM projects ORDER BY project_id";
        using var r = p.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void Dispose() { }

    private sealed class BundleFile
    {
        public string Format { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string? Name { get; set; }
        public string? CaseId { get; set; }
        public List<BundleBranch>? Branches { get; set; }
    }
    private sealed class BundleBranch
    {
        public string BranchId { get; set; } = "";
        public string? ParentBranchId { get; set; }
        public int ForkSeq { get; set; }
        public string? Reason { get; set; }
        public List<BundleEvent> Events { get; set; } = new();
    }
    private sealed class BundleEvent
    {
        public string EventId { get; set; } = "";
        public int Seq { get; set; }
        public string? Author { get; set; }
        public string CreatedAtUtc { get; set; } = "";
        public string? InputDigest { get; set; }
        public string? ParentEventId { get; set; }
        public string? EventType { get; set; }
        public JsonElement Payload { get; set; }
    }
}

public sealed class BadBundleException : Exception
{
    public BadBundleException(string message) : base(message) { }
}
public sealed class CrashSimulationException : Exception
{
    public CrashSimulationException() : base("simulated crash during import") { }
}
