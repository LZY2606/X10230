using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace AgeDepth;

/// <summary>One immutable, machine-readable event. Every judgment is an event.</summary>
public sealed record EventRec(
    long Seq, string Id, string Kind, string Branch, string[] Parents,
    string Digest, int ParamVersion, string PayloadJson, string[] Affected, string CreatedAt);

public enum ImportStatus { Ok, FormatCorrupt, Failed }

public sealed record ImportResult(ImportStatus Status, string Message, int EventCount)
{
    public FailureKind? AsFailureKind => Status switch
    {
        ImportStatus.FormatCorrupt => FailureKind.FormatCorrupt,
        ImportStatus.Failed => FailureKind.SemanticUnsolvable,
        _ => null
    };
}

/// <summary>
/// SQLite-backed event store. Events are the source of truth; imports are a single
/// transaction so a half-written import can never leave a mixed state behind.
/// </summary>
public sealed class Store : IDisposable
{
    readonly SqliteConnection _conn;

    public Store(string path)
    {
        _conn = new SqliteConnection(path == ":memory:" ? "Data Source=:memory:" : $"Data Source={path}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              id TEXT NOT NULL UNIQUE,
              kind TEXT NOT NULL,
              branch TEXT NOT NULL,
              parents TEXT NOT NULL,
              digest TEXT NOT NULL,
              param_version INTEGER NOT NULL,
              payload TEXT NOT NULL,
              affected TEXT NOT NULL,
              created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public static Store InMemory() => new(":memory:");

    public void Dispose() => _conn.Dispose();

    public long AppendEvent(string id, string kind, string branch, string[] parents,
        string digest, int paramVersion, string payloadJson, string[] affected, string createdAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(id, kind, branch, parents, digest, param_version, payload, affected, created)
            VALUES ($id, $kind, $branch, $parents, $digest, $pv, $payload, $affected, $created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$branch", branch);
        cmd.Parameters.AddWithValue("$parents", JsonSerializer.Serialize(parents));
        cmd.Parameters.AddWithValue("$digest", digest);
        cmd.Parameters.AddWithValue("$pv", paramVersion);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.Parameters.AddWithValue("$affected", JsonSerializer.Serialize(affected));
        cmd.Parameters.AddWithValue("$created", createdAt);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public List<EventRec> ReadEvents(long? upToSeq = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = upToSeq is null
            ? "SELECT seq,id,kind,branch,parents,digest,param_version,payload,affected,created FROM events ORDER BY seq"
            : "SELECT seq,id,kind,branch,parents,digest,param_version,payload,affected,created FROM events WHERE seq <= $upto ORDER BY seq";
        if (upToSeq is not null) cmd.Parameters.AddWithValue("$upto", upToSeq.Value);
        var list = new List<EventRec>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventRec(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? [],
                r.GetString(5), r.GetInt32(6), r.GetString(7),
                JsonSerializer.Deserialize<string[]>(r.GetString(8)) ?? [],
                r.GetString(9)));
        }
        return list;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, string> ReadMeta()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key,value FROM meta ORDER BY key";
        var d = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public string ExportJson()
    {
        var events = ReadEvents();
        var arr = new JsonArray();
        foreach (var e in events)
        {
            arr.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["kind"] = e.Kind,
                ["branch"] = e.Branch,
                ["parents"] = new JsonArray(e.Parents.Select(p => JsonValue.Create(p)).ToArray<JsonNode>()),
                ["digest"] = e.Digest,
                ["paramVersion"] = e.ParamVersion,
                ["payload"] = JsonNode.Parse(e.PayloadJson),
                ["affected"] = new JsonArray(e.Affected.Select(a => JsonValue.Create(a)).ToArray<JsonNode>()),
                ["created"] = e.CreatedAt
            });
        }
        var meta = new JsonObject();
        foreach (var (k, v) in ReadMeta()) meta[k] = v;
        var bundle = new JsonObject
        {
            ["format"] = "agedepth-bundle/1",
            ["meta"] = meta,
            ["events"] = arr
        };
        return bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Atomic import: validates the whole bundle first, then replaces the event log in a
    /// single transaction. A corrupt bundle, a duplicate identity or any mid-write failure
    /// rolls everything back, so the previous state survives a half-written import.
    /// </summary>
    public ImportResult ImportJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return new(ImportStatus.FormatCorrupt, $"invalid JSON: {ex.Message}", 0); }

        using var _ = doc;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var fmt) || fmt.GetString() != "agedepth-bundle/1")
            return new(ImportStatus.FormatCorrupt, "missing or unknown 'format' (expected agedepth-bundle/1)", 0);
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return new(ImportStatus.FormatCorrupt, "missing 'events' array", 0);

        var parsed = new List<(string id, string kind, string branch, string parents, string digest,
            int pv, string payload, string affected, string created)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var ev in events.EnumerateArray())
        {
            string? Need(string name)
                => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Need("id"); var kind = Need("kind");
            if (id is null || kind is null)
                return new(ImportStatus.FormatCorrupt, $"events[{i}] lacks required 'id'/'kind'", 0);
            if (!ev.TryGetProperty("payload", out var payload))
                return new(ImportStatus.FormatCorrupt, $"events[{i}] ('{id}') lacks 'payload'", 0);
            if (!seen.Add(id))
                return new(ImportStatus.FormatCorrupt, $"duplicate event identity '{id}' at events[{i}]", 0);
            var parents = ev.TryGetProperty("parents", out var p) ? p.GetRawText() : "[]";
            var affected = ev.TryGetProperty("affected", out var a) ? a.GetRawText() : "[]";
            var pv = ev.TryGetProperty("paramVersion", out var pvEl) && pvEl.ValueKind == JsonValueKind.Number
                ? pvEl.GetInt32() : 1;
            parsed.Add((id, kind, Need("branch") ?? "main", parents, Need("digest") ?? "",
                pv, payload.GetRawText(), affected, Need("created") ?? ""));
            i++;
        }

        var meta = new Dictionary<string, string>();
        if (root.TryGetProperty("meta", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
            foreach (var kv in metaEl.EnumerateObject())
                meta[kv.Name] = kv.Value.GetString() ?? "";

        using var tx = _conn.BeginTransaction();
        try
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM events; DELETE FROM meta;";
                cmd.ExecuteNonQuery();
            }
            foreach (var e in parsed)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO events(id, kind, branch, parents, digest, param_version, payload, affected, created)
                    VALUES ($id, $kind, $branch, $parents, $digest, $pv, $payload, $affected, $created);
                    """;
                cmd.Parameters.AddWithValue("$id", e.id);
                cmd.Parameters.AddWithValue("$kind", e.kind);
                cmd.Parameters.AddWithValue("$branch", e.branch);
                cmd.Parameters.AddWithValue("$parents", e.parents);
                cmd.Parameters.AddWithValue("$digest", e.digest);
                cmd.Parameters.AddWithValue("$pv", e.pv);
                cmd.Parameters.AddWithValue("$payload", e.payload);
                cmd.Parameters.AddWithValue("$affected", e.affected);
                cmd.Parameters.AddWithValue("$created", e.created);
                cmd.ExecuteNonQuery();
            }
            foreach (var (k, v) in meta)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v)";
                cmd.Parameters.AddWithValue("$k", k);
                cmd.Parameters.AddWithValue("$v", v);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.Rollback();
            return new(ImportStatus.Failed, $"import rolled back, previous state intact: {ex.Message}", 0);
        }
        return new(ImportStatus.Ok, $"imported {parsed.Count} events", parsed.Count);
    }

    public void Clear()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events; DELETE FROM meta;";
        cmd.ExecuteNonQuery();
    }
}
