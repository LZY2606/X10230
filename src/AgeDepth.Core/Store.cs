using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

public sealed record StoredEvent(long Seq, string Id, string Type, string Payload, string? InputDigest, string? ParamVersion, string Parents, string CreatedUtc);
public sealed record StoredRun(string Id, long EventSeq, string InputDigest, string ParamVersion, string ParentRun, string Affected, string Status, string ResultJson);

public enum FreezeOutcome { Frozen, Conflict }
public sealed record FreezeResult(FreezeOutcome Outcome, StoredEvent? Event);
public sealed record ImportResult(FailureKind Failure, IReadOnlyList<Diagnostic> Diagnostics, int Imported = 0)
{
    public bool Ok => Failure == FailureKind.None;
}

/// <summary>Append-only SQLite event store; also persists per-event model runs for time travel.</summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _db;

    public EventStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS materials (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              payload TEXT NOT NULL,
              content_hash TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS events (
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE,
              type TEXT NOT NULL,
              payload TEXT NOT NULL,
              input_digest TEXT,
              param_version TEXT,
              parents TEXT NOT NULL,
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
              id TEXT PRIMARY KEY,
              event_seq INTEGER NOT NULL,
              input_digest TEXT NOT NULL,
              param_version TEXT NOT NULL,
              parent_run TEXT NOT NULL,
              affected TEXT NOT NULL,
              status TEXT NOT NULL,
              result_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS freezes (
              model_id TEXT PRIMARY KEY,
              run_id TEXT NOT NULL,
              event_seq INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string name, string value) => cmd.Parameters.AddWithValue(name, value);

    private long LastSeq()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public StoredEvent Append(string type, JsonNode payload, IEnumerable<string>? parents = null,
        string? paramVersion = null, string? inputDigest = null)
    {
        var parentList = parents?.Where(x => x is not null).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList() ?? new List<string>();
        var parentsJson = Canon.Serialize(new JsonArray(parentList.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()));
        var canonical = Canon.Serialize(new JsonObject
        {
            ["type"] = type,
            ["payload"] = payload.DeepClone(),
            ["parents"] = JsonNode.Parse(parentsJson)!,
            ["nonce"] = (LastSeq() + 1).ToString()
        });
        var id = "evt-" + Canon.Digest(canonical)[..16];
        var ts = DateTime.UtcNow.ToString("o");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(id,type,payload,input_digest,param_version,parents,created_utc)
            VALUES($id,$type,$payload,$digest,$pv,$parents,$ts)
            """;
        Bind(cmd, "$id", id); Bind(cmd, "$type", type);
        cmd.Parameters.AddWithValue("$payload", Canon.Serialize(payload));
        cmd.Parameters.AddWithValue("$digest", (object?)inputDigest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pv", (object?)paramVersion ?? DBNull.Value);
        Bind(cmd, "$parents", parentsJson); Bind(cmd, "$ts", ts);
        cmd.ExecuteNonQuery();
        return new StoredEvent(LastSeq(), id, type, Canon.Serialize(payload), inputDigest, paramVersion, parentsJson, ts);
    }

    public void AddMaterial(string id, string kind, JsonNode payload)
    {
        var json = Canon.Serialize(payload);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO materials(id,kind,payload,content_hash) VALUES($id,$kind,$p,$h)";
        Bind(cmd, "$id", id); Bind(cmd, "$kind", kind);
        Bind(cmd, "$p", json); Bind(cmd, "$h", Canon.Digest(json));
        cmd.ExecuteNonQuery();
    }

    public void ReplaceMaterialPayload(string id, JsonNode payload)
    {
        var json = Canon.Serialize(payload);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE materials SET payload=$p, content_hash=$h WHERE id=$id";
        Bind(cmd, "$p", json); Bind(cmd, "$h", Canon.Digest(json)); Bind(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    public (string id, string kind, string payload)? GetMaterial(string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,kind,payload FROM materials WHERE id=$id";
        Bind(cmd, "$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }

    public List<(string id, string kind, string payload, string hash)> Materials()
    {
        var list = new List<(string, string, string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,kind,payload,content_hash FROM materials ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public List<StoredEvent> Events(long? upToSeq = null)
    {
        var list = new List<StoredEvent>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,type,payload,input_digest,param_version,parents,created_utc FROM events ORDER BY seq"
            : "SELECT seq,id,type,payload,input_digest,param_version,parents,created_utc FROM events WHERE seq<=$n ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$n", upToSeq.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6), r.GetString(7)));
        return list;
    }

    public List<StoredRun> RunsAll()
    {
        var list = new List<StoredRun>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,event_seq,input_digest,param_version,parent_run,affected,status,result_json FROM runs ORDER BY event_seq,rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredRun(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetString(7)));
        return list;
    }

    public StoredRun? LatestRun(long? upToSeq = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT id,event_seq,input_digest,param_version,parent_run,affected,status,result_json FROM runs ORDER BY event_seq DESC,rowid DESC LIMIT 1"
            : "SELECT id,event_seq,input_digest,param_version,parent_run,affected,status,result_json FROM runs WHERE event_seq<=$n ORDER BY event_seq DESC,rowid DESC LIMIT 1";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$n", upToSeq.Value);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new StoredRun(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetString(7));
    }

    public StoredRun? GetRun(string id) => RunsAll().FirstOrDefault(r => r.Id == id);

    public void SaveRun(StoredRun run)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs(id,event_seq,input_digest,param_version,parent_run,affected,status,result_json)
            VALUES($id,$seq,$d,$pv,$parent,$aff,$status,$json)
            """;
        Bind(cmd, "$id", run.Id);
        cmd.Parameters.AddWithValue("$seq", run.EventSeq);
        Bind(cmd, "$d", run.InputDigest); Bind(cmd, "$pv", run.ParamVersion);
        Bind(cmd, "$parent", run.ParentRun); Bind(cmd, "$aff", run.Affected);
        Bind(cmd, "$status", run.Status); Bind(cmd, "$json", run.ResultJson);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Serializes concurrent freezes on one model: first commit wins, others get Conflict.</summary>
    public FreezeResult Freeze(string modelId, string runId, long eventSeq)
    {
        using var tx = _db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using (var check = _db.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM freezes WHERE model_id=$id";
            Bind(check, "$id", modelId);
            if (check.ExecuteScalar() is not null)
            {
                tx.Rollback();
                return new FreezeResult(FreezeOutcome.Conflict, null);
            }
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO freezes(model_id,run_id,event_seq) VALUES($id,$run,$seq)";
            Bind(ins, "$id", modelId); Bind(ins, "$run", runId);
            ins.Parameters.AddWithValue("$seq", eventSeq);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        var ev = Append("model.frozen", new JsonObject
        {
            ["modelId"] = modelId, ["runId"] = runId, ["atSeq"] = eventSeq
        });
        return new FreezeResult(FreezeOutcome.Frozen, ev);
    }

    public (string modelId, string runId, long eventSeq)? FrozenModel(string modelId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT model_id,run_id,event_seq FROM freezes WHERE model_id=$id";
        Bind(cmd, "$id", modelId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    public List<(string modelId, string runId, long eventSeq)> Freezes()
    {
        var list = new List<(string, string, long)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT model_id,run_id,event_seq FROM freezes ORDER BY event_seq";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return list;
    }

    public string Export()
    {
        var root = new JsonObject
        {
            ["format"] = "agedepth-export",
            ["version"] = 1,
            ["materials"] = new JsonArray(Materials().Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.id, ["kind"] = m.kind,
                ["payload"] = JsonNode.Parse(m.payload)!, ["contentHash"] = m.hash
            }).ToArray()),
            ["events"] = new JsonArray(Events().Select(e => (JsonNode)new JsonObject
            {
                ["seq"] = e.Seq, ["id"] = e.Id, ["type"] = e.Type,
                ["payload"] = JsonNode.Parse(e.Payload)!,
                ["inputDigest"] = e.InputDigest, ["paramVersion"] = e.ParamVersion,
                ["parents"] = JsonNode.Parse(e.Parents)!
            }).ToArray()),
            ["runs"] = new JsonArray(RunsAll().Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["eventSeq"] = r.EventSeq, ["inputDigest"] = r.InputDigest,
                ["paramVersion"] = r.ParamVersion, ["parentRun"] = r.ParentRun,
                ["affected"] = JsonNode.Parse(r.Affected)!, ["status"] = r.Status,
                ["resultJson"] = JsonNode.Parse(r.ResultJson)!
            }).ToArray()),
            ["freezes"] = new JsonArray(Freezes().Select(f => (JsonNode)new JsonObject
            {
                ["modelId"] = f.modelId, ["runId"] = f.runId, ["eventSeq"] = f.eventSeq
            }).ToArray())
        };
        return Canon.Serialize(root);
    }

    /// <summary>
    /// Import an export bundle. Distinct failures: FormatCorrupt (bad JSON/shape),
    /// SemanticUnsolvable (hash/parent inconsistency), ResourceBudgetExhausted (too many
    /// events). Everything lands in one transaction; <paramref name="crashAfter"/>
    /// simulates a crash mid-write so half-written imports can be verified to roll back.
    /// </summary>
    public ImportResult Import(string json, int maxEvents = 100_000, Action<int>? crashAfter = null)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
                return Fail(FailureKind.FormatCorrupt, "import.format", "top-level JSON is not an object", "");
            if (root["format"]?.GetValue<string>() != "agedepth-export" || root["version"]?.GetValue<int>() != 1)
                return Fail(FailureKind.FormatCorrupt, "import.format", "missing/unknown format or version", "");
        }
        catch (JsonException ex)
        {
            return Fail(FailureKind.FormatCorrupt, "import.format", "malformed JSON: " + ex.Message, "");
        }

        JsonArray mats, events, runs, freezes;
        try
        {
            mats = root["materials"] as JsonArray ?? throw new JsonException("materials must be an array");
            events = root["events"] as JsonArray ?? throw new JsonException("events must be an array");
            runs = root["runs"] as JsonArray ?? new JsonArray();
            freezes = root["freezes"] as JsonArray ?? new JsonArray();
        }
        catch (JsonException ex)
        {
            return Fail(FailureKind.FormatCorrupt, "import.format", ex.Message, "");
        }

        if (events.Count > maxEvents)
            return Fail(FailureKind.ResourceBudgetExhausted, "import.budget",
                $"event count {events.Count} exceeds budget {maxEvents}", "events");

        try
        {
            foreach (var m in mats)
            {
                var id = m!["id"]?.GetValue<string>() ?? throw new JsonException("material missing id");
                var payload = Canon.Serialize(m["payload"]!);
                if (m["contentHash"]?.GetValue<string>() != Canon.Digest(payload))
                    throw new JsonException($"material {id} content hash mismatch");
            }
            var knownIds = new HashSet<string>(events.Select(e => e!["id"]!.GetValue<string>()));
            long prevSeq = 0;
            foreach (var e in events)
            {
                if (e!["parents"] is not JsonArray parents) throw new JsonException("parents must be an array");
                foreach (var p in parents)
                    if (!knownIds.Contains(p!.GetValue<string>()))
                        throw new JsonException($"event {e["id"]} references unknown parent {p}");
                var seq = e["seq"]!.GetValue<long>();
                if (seq != ++prevSeq) throw new JsonException($"event seq gap or reorder near {seq}");
            }
        }
        catch (JsonException ex)
        {
            return Fail(FailureKind.SemanticUnsolvable, "import.semantic", ex.Message, "");
        }

        var inserted = 0;
        try
        {
            using var tx = _db.BeginTransaction(System.Data.IsolationLevel.Serializable);
            void Step()
            {
                inserted++;
                crashAfter?.Invoke(inserted);
            }
            foreach (var m in mats)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO materials(id,kind,payload,content_hash) VALUES($id,$kind,$p,$h)";
                Bind(cmd, "$id", m!["id"]!.GetValue<string>());
                Bind(cmd, "$kind", m["kind"]!.GetValue<string>());
                var payload = Canon.Serialize(m["payload"]!);
                Bind(cmd, "$p", payload); Bind(cmd, "$h", Canon.Digest(payload));
                cmd.ExecuteNonQuery();
                Step();
            }
            foreach (var e in events)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO events(seq,id,type,payload,input_digest,param_version,parents,created_utc)
                    VALUES($seq,$id,$type,$payload,$digest,$pv,$parents,$ts)
                    """;
                cmd.Parameters.AddWithValue("$seq", e!["seq"]!.GetValue<long>());
                Bind(cmd, "$id", e["id"]!.GetValue<string>());
                Bind(cmd, "$type", e["type"]!.GetValue<string>());
                Bind(cmd, "$payload", Canon.Serialize(e["payload"]!));
                cmd.Parameters.AddWithValue("$digest", (object?)e["inputDigest"]?.GetValue<string>() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pv", (object?)e["paramVersion"]?.GetValue<string>() ?? DBNull.Value);
                Bind(cmd, "$parents", Canon.Serialize(e["parents"]!));
                Bind(cmd, "$ts", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
                Step();
            }
            foreach (var r in runs)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO runs(id,event_seq,input_digest,param_version,parent_run,affected,status,result_json)
                    VALUES($id,$seq,$d,$pv,$parent,$aff,$status,$json)
                    """;
                Bind(cmd, "$id", r!["id"]!.GetValue<string>());
                cmd.Parameters.AddWithValue("$seq", r["eventSeq"]!.GetValue<long>());
                Bind(cmd, "$d", r["inputDigest"]!.GetValue<string>());
                Bind(cmd, "$pv", r["paramVersion"]!.GetValue<string>());
                Bind(cmd, "$parent", r["parentRun"]!.GetValue<string>());
                Bind(cmd, "$aff", Canon.Serialize(r["affected"]!));
                Bind(cmd, "$status", r["status"]!.GetValue<string>());
                Bind(cmd, "$json", Canon.Serialize(r["resultJson"]!));
                cmd.ExecuteNonQuery();
                Step();
            }
            foreach (var f in freezes)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO freezes(model_id,run_id,event_seq) VALUES($id,$run,$seq)";
                Bind(cmd, "$id", f!["modelId"]!.GetValue<string>());
                Bind(cmd, "$run", f["runId"]!.GetValue<string>());
                cmd.Parameters.AddWithValue("$seq", f["eventSeq"]!.GetValue<long>());
                cmd.ExecuteNonQuery();
                Step();
            }
            tx.Commit();
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            return Fail(FailureKind.FormatCorrupt, "import.write", "import rolled back: " + ex.Message, "");
        }
        return new ImportResult(FailureKind.None, Array.Empty<Diagnostic>(), inserted);
    }

    private static ImportResult Fail(FailureKind kind, string code, string message, string position) =>
        new(kind, new[] { new Diagnostic(code, message, position) });

    public void Dispose() => _db.Dispose();
}
