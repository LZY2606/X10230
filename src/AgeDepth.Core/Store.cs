using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeDepth.Core;

/// <summary>
/// SQLite-backed store. Immutable materials, append-only events, model runs with
/// two-phase status (writing -> complete) so half-written runs are recovered on open.
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Store(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _conn = new SqliteConnection(builder.ConnectionString);
        _conn.Open();
        Initialize();
        RecoverAbortedRuns();
    }

    private void Initialize()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS materials(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL,
                UNIQUE(kind, id));
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT UNIQUE, type TEXT NOT NULL, payload TEXT NOT NULL,
                parent TEXT, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(
                id TEXT PRIMARY KEY, status TEXT NOT NULL, param_version TEXT NOT NULL,
                input_digest TEXT NOT NULL, parent_id TEXT, payload TEXT NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS candidates(
                candidate_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, kind TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'proposed');
            CREATE TABLE IF NOT EXISTS model_state(
                model_id TEXT PRIMARY KEY, version INTEGER NOT NULL, frozen INTEGER NOT NULL DEFAULT 0);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Half-written runs (status 'writing' left by a crash) are marked aborted on open.</summary>
    public int RecoverAbortedRuns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET status='aborted' WHERE status='writing'";
        return cmd.ExecuteNonQuery();
    }

    public void UpsertMaterial(string kind, string id, string payloadJson)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO materials(kind, id, payload) VALUES($k, $i, $p)
                ON CONFLICT(kind, id) DO UPDATE SET payload=excluded.payload;
                """;
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$i", id);
            cmd.Parameters.AddWithValue("$p", payloadJson);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(string Id, string Payload)> Materials(string kind)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, payload FROM materials WHERE kind=$k ORDER BY seq";
            cmd.Parameters.AddWithValue("$k", kind);
            var list = new List<(string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add((reader.GetString(0), reader.GetString(1)));
            return list;
        }
    }

    public IReadOnlyList<string> MaterialKinds()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT kind FROM materials ORDER BY kind";
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    public (long Seq, string Id) AppendEvent(string type, string payloadJson, string? parent = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO events(id, type, payload, parent, created) VALUES(NULL, $t, $p, $pa, $c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", type);
            cmd.Parameters.AddWithValue("$p", payloadJson);
            cmd.Parameters.AddWithValue("$pa", (object?)parent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
            var seq = (long)cmd.ExecuteScalar()!;
            var id = $"evt:{seq:D6}";
            using var upd = _conn.CreateCommand();
            upd.CommandText = "UPDATE events SET id=$i WHERE seq=$s";
            upd.Parameters.AddWithValue("$i", id);
            upd.Parameters.AddWithValue("$s", seq);
            upd.ExecuteNonQuery();
            return (seq, id);
        }
    }

    public IReadOnlyList<(long Seq, string Id, string Type, string Payload, string? Parent)> Events()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, id, type, payload, parent FROM events ORDER BY seq";
            var list = new List<(long, string, string, string, string?)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            return list;
        }
    }

    /// <summary>Two-phase run write: 'writing' row first, payload, then 'complete', all in one transaction.</summary>
    public void SaveRun(ModelRunResult run, string payloadJson)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            InsertRunRow(tx, run.RunId, "writing", run.ParamVersion, run.InputDigest, run.ParentRunId, "{}");
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE runs SET status='complete', payload=$p WHERE id=$i";
                cmd.Parameters.AddWithValue("$p", payloadJson);
                cmd.Parameters.AddWithValue("$i", run.RunId);
                cmd.ExecuteNonQuery();
            }
            foreach (var candidate in run.Candidates)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO candidates(candidate_id, run_id, kind, status) VALUES($c, $r, $k, 'proposed')
                    ON CONFLICT(candidate_id) DO NOTHING;
                    """;
                cmd.Parameters.AddWithValue("$c", candidate.CandidateId);
                cmd.Parameters.AddWithValue("$r", run.RunId);
                cmd.Parameters.AddWithValue("$k", candidate.Kind);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Low-level insert used by tests to simulate a crash mid-write.</summary>
    public void InsertRunRow(SqliteTransaction? tx, string id, string status, string paramVersion,
        string inputDigest, string? parentId, string payload)
    {
        using var cmd = _conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runs(id, status, param_version, input_digest, parent_id, payload, created)
            VALUES($i, $s, $pv, $d, $pa, $p, $c)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status, payload=excluded.payload;
            """;
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$pv", paramVersion);
        cmd.Parameters.AddWithValue("$d", inputDigest);
        cmd.Parameters.AddWithValue("$pa", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public string? RunPayload(string id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM runs WHERE id=$i AND status='complete'";
            cmd.Parameters.AddWithValue("$i", id);
            return cmd.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<(string Id, string Status, string ParamVersion, string InputDigest, string? ParentId)> Runs()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, status, param_version, input_digest, parent_id FROM runs ORDER BY rowid";
            var list = new List<(string, string, string, string, string?)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            return list;
        }
    }

    public IReadOnlyList<(string CandidateId, string RunId, string Kind, string Status)> Candidates()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT candidate_id, run_id, kind, status FROM candidates ORDER BY rowid";
            var list = new List<(string, string, string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return list;
        }
    }

    public void SetCandidateStatus(string candidateId, string status)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE candidates SET status=$s WHERE candidate_id=$c";
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$c", candidateId);
            cmd.ExecuteNonQuery();
        }
    }

    public (int Version, bool Frozen) ModelState(string modelId)
    {
        lock (_gate)
        {
            EnsureModelState(modelId);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT version, frozen FROM model_state WHERE model_id=$m";
            cmd.Parameters.AddWithValue("$m", modelId);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1) == 1);
        }
    }

    private void EnsureModelState(string modelId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO model_state(model_id, version, frozen) VALUES($m, 0, 0)";
        cmd.Parameters.AddWithValue("$m", modelId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Optimistic-concurrency freeze: exactly one concurrent caller with the same version wins.</summary>
    public bool TrySetFrozen(string modelId, int expectedVersion, bool frozen)
    {
        lock (_gate)
        {
            EnsureModelState(modelId);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE model_state SET frozen=$f, version=version+1
                WHERE model_id=$m AND version=$v;
                """;
            cmd.Parameters.AddWithValue("$f", frozen ? 1 : 0);
            cmd.Parameters.AddWithValue("$m", modelId);
            cmd.Parameters.AddWithValue("$v", expectedVersion);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public long Count(string table)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM materials; DELETE FROM events; DELETE FROM runs; DELETE FROM candidates; DELETE FROM model_state; DELETE FROM meta;";
            cmd.ExecuteNonQuery();
        }
    }

    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    /// <summary>Deterministic export: stable identities, order and provenance survive a round trip.</summary>
    public string Export()
    {
        lock (_gate)
        {
            var doc = new
            {
                format = "agedepth-export",
                version = 1,
                materials = MaterialKinds().SelectMany(k =>
                    Materials(k).Select(m => new { kind = k, id = m.Id, payload = JsonSerializer.Deserialize<JsonElement>(m.Payload) })).ToArray(),
                events = Events().Select(e => new { e.Id, e.Type, payload = JsonSerializer.Deserialize<JsonElement>(e.Payload), e.Parent }).ToArray(),
                runs = Runs().Select(r => new
                {
                    r.Id, r.Status, r.ParamVersion, r.InputDigest, r.ParentId,
                    payload = JsonSerializer.Deserialize<JsonElement>(RunPayload(r.Id) ?? (r.Status == "complete" ? "{}" : "{}"))
                }).ToArray(),
                candidates = Candidates().Select(c => new { c.CandidateId, c.RunId, c.Kind, c.Status }).ToArray(),
            };
            return JsonSerializer.Serialize(doc, ExportOptions);
        }
    }

    public Result<(int Materials, int Events, int Runs)> Import(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Result.Failure<(int, int, int)>(ResultKind.FormatCorrupt,
                new Diagnostic("import.format-corrupt", $"Export document is not valid JSON: {ex.Message}", "import:body"));
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "agedepth-export")
            {
                return Result.Failure<(int, int, int)>(ResultKind.FormatCorrupt,
                    new Diagnostic("import.format-corrupt", "Missing or unknown 'format' marker; expected 'agedepth-export'.", "import:format"));
            }
            var materials = 0;
            var events = 0;
            var runs = 0;
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                if (root.TryGetProperty("materials", out var mats))
                {
                    foreach (var m in mats.EnumerateArray())
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO materials(kind, id, payload) VALUES($k, $i, $p) ON CONFLICT(kind, id) DO UPDATE SET payload=excluded.payload;";
                        cmd.Parameters.AddWithValue("$k", m.GetProperty("kind").GetString()!);
                        cmd.Parameters.AddWithValue("$i", m.GetProperty("id").GetString()!);
                        cmd.Parameters.AddWithValue("$p", m.GetProperty("payload").GetRawText());
                        cmd.ExecuteNonQuery();
                        materials++;
                    }
                }
                if (root.TryGetProperty("events", out var evts))
                {
                    foreach (var e in evts.EnumerateArray())
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            INSERT OR IGNORE INTO events(id, type, payload, parent, created)
                            VALUES($i, $t, $p, $pa, $c);
                            """;
                        cmd.Parameters.AddWithValue("$i", e.GetProperty("Id").GetString()!);
                        cmd.Parameters.AddWithValue("$t", e.GetProperty("Type").GetString()!);
                        cmd.Parameters.AddWithValue("$p", e.GetProperty("payload").GetRawText());
                        cmd.Parameters.AddWithValue("$pa",
                            e.TryGetProperty("Parent", out var pa) && pa.ValueKind == JsonValueKind.String
                                ? pa.GetString()! : DBNull.Value);
                        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
                        cmd.ExecuteNonQuery();
                        events++;
                    }
                }
                if (root.TryGetProperty("runs", out var rns))
                {
                    foreach (var r in rns.EnumerateArray())
                    {
                        InsertRunRow(tx,
                            r.GetProperty("Id").GetString()!,
                            r.GetProperty("Status").GetString()!,
                            r.GetProperty("ParamVersion").GetString()!,
                            r.GetProperty("InputDigest").GetString()!,
                            r.TryGetProperty("ParentId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null,
                            r.GetProperty("payload").GetRawText());
                        runs++;
                    }
                }
                if (root.TryGetProperty("candidates", out var cands))
                {
                    foreach (var c in cands.EnumerateArray())
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            INSERT INTO candidates(candidate_id, run_id, kind, status) VALUES($c, $r, $k, $s)
                            ON CONFLICT(candidate_id) DO UPDATE SET status=excluded.status;
                            """;
                        cmd.Parameters.AddWithValue("$c", c.GetProperty("CandidateId").GetString()!);
                        cmd.Parameters.AddWithValue("$r", c.GetProperty("RunId").GetString()!);
                        cmd.Parameters.AddWithValue("$k", c.GetProperty("Kind").GetString()!);
                        cmd.Parameters.AddWithValue("$s", c.GetProperty("Status").GetString()!);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            return Result.Success((materials, events, runs));
        }
    }

    public void Dispose() => _conn.Dispose();
}
