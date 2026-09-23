using Microsoft.Data.Sqlite;
using DepthAge.Domain;

namespace DepthAge.Storage;

/// <summary>
/// SQLite-backed append-only event store. Events form a tamper-evident hash chain;
/// every append verifies inside a transaction. Import replays a bundle in one transaction
/// (rollback on any corrupt record), and derivations are content-addressed.
/// </summary>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _writeLock = new();

    public SqliteEventStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        Schema();
    }

    private void Schema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
CREATE TABLE IF NOT EXISTS events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  event_id TEXT NOT NULL,
  branch_id TEXT NOT NULL,
  actor TEXT NOT NULL,
  ts TEXT NOT NULL,
  payload_type TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  correlation_id TEXT,
  cause_event_id TEXT,
  hash TEXT NOT NULL,
  prev_hash TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_events_event_id ON events(event_id);
CREATE INDEX IF NOT EXISTS ix_events_branch ON events(branch_id, seq);
CREATE TABLE IF NOT EXISTS derivations (
  derivation_id TEXT PRIMARY KEY,
  json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS reports (
  kind TEXT NOT NULL,
  report_id TEXT NOT NULL,
  json TEXT NOT NULL,
  PRIMARY KEY (kind, report_id)
);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
""";
        cmd.ExecuteNonQuery();
    }

    public long LastSeq
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public long Append(EventEnvelope envelope)
    {
        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            var seq = InsertEvent(envelope, tx);
            tx.Commit();
            return seq;
        }
    }

    public long AppendIfUnchanged(EventEnvelope envelope, long expectedSeq)
    {
        lock (_writeLock)
        {
            using var tx = _connection.BeginTransaction();
            var branchLast = LastSeqForBranch(envelope.BranchId, tx);
            if (branchLast != expectedSeq)
            {
                tx.Rollback();
                throw new ConcurrencyConflictException(expectedSeq, branchLast);
            }
            var seq = InsertEvent(envelope, tx);
            tx.Commit();
            return seq;
        }
    }

    private long InsertEvent(EventEnvelope envelope, SqliteTransaction tx)
    {
        var payloadJson = Canon.Serialize(envelope.Payload);
        var prevHash = LastHash(tx);
        var hash = Canon.Sha256Hex(prevHash + payloadJson);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
INSERT INTO events (event_id, branch_id, actor, ts, payload_type, payload_json,
                     correlation_id, cause_event_id, hash, prev_hash)
VALUES ($id, $branch, $actor, $ts, $type, $json, $corr, $cause, $hash, $prev)
""";
        Add(cmd, "$id", envelope.EventId);
        Add(cmd, "$branch", envelope.BranchId);
        Add(cmd, "$actor", envelope.Actor);
        Add(cmd, "$ts", envelope.Timestamp);
        Add(cmd, "$type", envelope.PayloadKind);
        Add(cmd, "$json", payloadJson);
        Add(cmd, "$corr", (object?)envelope.CorrelationId ?? DBNull.Value);
        Add(cmd, "$cause", (object?)envelope.CauseEventId ?? DBNull.Value);
        Add(cmd, "$hash", hash);
        Add(cmd, "$prev", prevHash);
        cmd.ExecuteNonQuery();
        return LastInsertRowId(tx);
    }

    private static long LastInsertRowId(SqliteTransaction tx)
    {
        using var cmd = tx.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    private string LastHash(SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT hash FROM events ORDER BY seq DESC LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is string s ? s : "GENESIS";
    }

    private long LastSeqForBranch(string branchId, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events WHERE branch_id = $b";
        Add(cmd, "$b", branchId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Add(SqliteCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // ---------------------------------------------------------------- reads

    public IReadOnlyList<StoredEvent> ReadAll()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT seq, event_id, branch_id, actor, ts, correlation_id, cause_event_id, payload_json FROM events ORDER BY seq";
        return Read(cmd);
    }

    public IReadOnlyList<StoredEvent> ReadBranch(string branchId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT seq, event_id, branch_id, actor, ts, correlation_id, cause_event_id, payload_json FROM events WHERE branch_id = $b ORDER BY seq";
        Add(cmd, "$b", branchId);
        return Read(cmd);
    }

    private static IReadOnlyList<StoredEvent> Read(SqliteCommand cmd)
    {
        var result = new List<StoredEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var seq = reader.GetInt64(0);
            var envelope = new EventEnvelope
            {
                EventId = reader.GetString(1),
                BranchId = reader.GetString(2),
                Actor = reader.GetString(3),
                Timestamp = reader.GetString(4),
                Payload = ReadPayload(reader.GetString(7)),
                CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CauseEventId = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
            result.Add(new StoredEvent { Seq = seq, Envelope = envelope, Hash = "" });
        }
        return result;
    }

    private static EventPayload ReadPayload(string json) =>
        Canon.Deserialize<EventPayload>(json)
        ?? throw new FormatCorruptException("events.payload", "payload deserialized to null");

    // ---------------------------------------------------------------- derivations

    public void SaveDerivation(DerivationRecord record)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO derivations (derivation_id, json) VALUES ($id, $json)";
            Add(cmd, "$id", record.DerivationId);
            Add(cmd, "$json", Canon.Serialize(record));
            cmd.ExecuteNonQuery();
        }
    }

    public DerivationRecord? GetDerivation(string derivationId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM derivations WHERE derivation_id = $id";
        Add(cmd, "$id", derivationId);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : Canon.Deserialize<DerivationRecord>(json);
    }

    public IReadOnlyList<DerivationRecord> AllDerivations()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM derivations";
        var result = new List<DerivationRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(Canon.Deserialize<DerivationRecord>(reader.GetString(0))!);
        return result;
    }

    // ---------------------------------------------------------------- reports

    public void SaveReport(string kind, string id, string json)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO reports (kind, report_id, json) VALUES ($k, $id, $j) " +
                "ON CONFLICT(kind, report_id) DO UPDATE SET json = excluded.json";
            Add(cmd, "$k", kind);
            Add(cmd, "$id", id);
            Add(cmd, "$j", json);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetReport(string kind, string id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM reports WHERE kind = $k AND report_id = $id";
        Add(cmd, "$k", kind);
        Add(cmd, "$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<(string Kind, string Id)> ListReports(string kind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT report_id FROM reports WHERE kind = $k ORDER BY report_id";
        Add(cmd, "$k", kind);
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((kind, reader.GetString(0)));
        return result;
    }

    // ---------------------------------------------------------------- export / import

    public string ExportBundleJson()
    {
        var rows = new List<object>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
SELECT seq, event_id, branch_id, actor, ts, payload_type, payload_json,
       correlation_id, cause_event_id, hash, prev_hash
FROM events ORDER BY seq
""";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new
                {
                    seq = reader.GetInt64(0),
                    eventId = reader.GetString(1),
                    branch = reader.GetString(2),
                    actor = reader.GetString(3),
                    ts = reader.GetString(4),
                    type = reader.GetString(5),
                    payload = reader.GetString(6),
                    correlationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    causeEventId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    hash = reader.GetString(9),
                    prevHash = reader.GetString(10)
                });
        }
        var derivations = new List<object>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT derivation_id, json FROM derivations ORDER BY derivation_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                derivations.Add(new { id = reader.GetString(0), json = reader.GetString(1) });
        }
        var reports = new List<object>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, report_id, json FROM reports ORDER BY kind, report_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                reports.Add(new { kind = reader.GetString(0), id = reader.GetString(1), json = reader.GetString(2) });
        }
        var bundle = new
        {
            format = "depth-age-bundle/v1",
            exportedAt = DateTime.UtcNow.ToString("o"),
            events = rows,
            derivations,
            reports
        };
        return Canon.Serialize(bundle);
    }

    public void ImportBundleJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format) ||
            format.GetString() != "depth-age-bundle/v1")
            throw new FormatCorruptException("bundle.format", "expected depth-age-bundle/v1");
        if (!root.TryGetProperty("events", out var eventsEl) ||
            eventsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new FormatCorruptException("bundle.events", "events array missing");

        lock (_writeLock)
        using (var tx = _connection.BeginTransaction())
        {
            foreach (var table in new[] { "events", "derivations", "reports", "meta" })
            {
                using var del = _connection.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {table}";
                del.ExecuteNonQuery();
            }

            var prevHash = "GENESIS";
            var expectedSeq = 1L;
            foreach (var row in eventsEl.EnumerateArray())
            {
                var payload = row.GetProperty("payload").GetString()!;
                var hash = Canon.Sha256Hex(prevHash + payload);
                if (row.GetProperty("hash").GetString() != hash)
                    throw new FormatCorruptException($"events[{expectedSeq}].hash", "hash chain verification failed");
                if (row.GetProperty("prevHash").GetString() != prevHash)
                    throw new FormatCorruptException($"events[{expectedSeq}].prevHash", "previous hash mismatch");
                if (row.GetProperty("seq").GetInt64() != expectedSeq)
                    throw new FormatCorruptException($"events[{expectedSeq}].seq", "sequence gap or reorder");
                try
                {
                    _ = Canon.Deserialize<EventPayload>(payload)
                        ?? throw new FormatCorruptException($"events[{expectedSeq}].payload", "payload is null");
                }
                catch (FormatCorruptException) { throw; }
                catch (Exception ex)
                {
                    throw new FormatCorruptException($"events[{expectedSeq}].payload", ex.Message);
                }

                using var insert = _connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
INSERT INTO events (event_id, branch_id, actor, ts, payload_type, payload_json,
                   correlation_id, cause_event_id, hash, prev_hash)
VALUES ($id, $branch, $actor, $ts, $type, $json, $corr, $cause, $hash, $prev)
""";
                Add(insert, "$id", row.GetProperty("eventId").GetString());
                Add(insert, "$branch", row.GetProperty("branch").GetString());
                Add(insert, "$actor", row.GetProperty("actor").GetString());
                Add(insert, "$ts", row.GetProperty("ts").GetString());
                Add(insert, "$type", row.GetProperty("type").GetString());
                Add(insert, "$json", payload);
                var correlation = row.GetProperty("correlationId");
                Add(insert, "$corr",
                    correlation.ValueKind == System.Text.Json.JsonValueKind.Null ? DBNull.Value : (object)correlation.GetString()!);
                var cause = row.GetProperty("causeEventId");
                Add(insert, "$cause",
                    cause.ValueKind == System.Text.Json.JsonValueKind.Null ? DBNull.Value : (object)cause.GetString()!);
                Add(insert, "$hash", hash);
                Add(insert, "$prev", prevHash);
                insert.ExecuteNonQuery();

                prevHash = hash;
                expectedSeq++;
            }

            if (root.TryGetProperty("derivations", out var derivationsEl))
            {
                foreach (var item in derivationsEl.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    var derivationJson = item.GetProperty("json").GetString()!;
                    try
                    {
                        _ = Canon.Deserialize<DerivationRecord>(derivationJson)
                            ?? throw new FormatCorruptException($"derivations[{id}]", "null derivation");
                    }
                    catch (FormatCorruptException) { throw; }
                    catch (Exception ex)
                    {
                        throw new FormatCorruptException($"derivations[{id}]", ex.Message);
                    }
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO derivations (derivation_id, json) VALUES ($id, $j)";
                    Add(insert, "$id", id);
                    Add(insert, "$j", derivationJson);
                    insert.ExecuteNonQuery();
                }
            }

            if (root.TryGetProperty("reports", out var reportsEl))
            {
                foreach (var item in reportsEl.EnumerateArray())
                {
                    using var insert = _connection.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText =
                        "INSERT INTO reports (kind, report_id, json) VALUES ($k, $id, $j)";
                    Add(insert, "$k", item.GetProperty("kind").GetString());
                    Add(insert, "$id", item.GetProperty("id").GetString());
                    Add(insert, "$j", item.GetProperty("json").GetString());
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
