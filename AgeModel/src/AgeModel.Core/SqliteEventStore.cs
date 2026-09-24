using Microsoft.Data.Sqlite;

namespace AgeModel.Core;

public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly string connectionString;
    private readonly SqliteConnection sharedConnection;

    public SqliteEventStore(string path)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
        sharedConnection = new SqliteConnection(connectionString);
        sharedConnection.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var command = sharedConnection.CreateCommand();
        command.CommandText = """
        CREATE TABLE IF NOT EXISTS events (
            event_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            timestamp_text TEXT NOT NULL,
            event_type TEXT NOT NULL,
            branch TEXT NOT NULL,
            parent_event_ids TEXT NOT NULL,
            rule_version TEXT NOT NULL,
            input_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            batch_id TEXT,
            PRIMARY KEY (project_id, event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_events_project_sequence ON events(project_id, sequence_number);
        CREATE TABLE IF NOT EXISTS write_intents (
            event_id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            batch_id TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS recovered_writes (
            event_id TEXT NOT NULL,
            project_id TEXT NOT NULL,
            sequence_number INTEGER NOT NULL,
            reason TEXT NOT NULL,
            recovered_at TEXT NOT NULL,
            PRIMARY KEY (event_id, project_id)
        );
        """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<RecoveredWrite> RecoverPendingWrites()
    {
        var recovered = new List<RecoveredWrite>();
        using var query = sharedConnection.CreateCommand();
        query.CommandText = "SELECT event_id, project_id, sequence_number FROM write_intents ORDER BY project_id, sequence_number";
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                recovered.Add(new RecoveredWrite(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    "write intent present without a committed event"));
            }
        }

        using var tx = sharedConnection.BeginTransaction();
        foreach (var item in recovered)
        {
            using var deleteEvent = sharedConnection.CreateCommand();
            deleteEvent.Transaction = tx;
            deleteEvent.CommandText = "DELETE FROM events WHERE project_id = $project AND event_id = $event";
            deleteEvent.Parameters.AddWithValue("$project", item.ProjectId);
            deleteEvent.Parameters.AddWithValue("$event", item.EventId);
            deleteEvent.ExecuteNonQuery();

            using var insert = sharedConnection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR REPLACE INTO recovered_writes(event_id, project_id, sequence_number, reason, recovered_at)
                VALUES ($event, $project, $sequence, $reason, $at)
            """;
            insert.Parameters.AddWithValue("$event", item.EventId);
            insert.Parameters.AddWithValue("$project", item.ProjectId);
            insert.Parameters.AddWithValue("$sequence", item.Sequence);
            insert.Parameters.AddWithValue("$reason", item.Reason);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();

            using var deleteIntent = sharedConnection.CreateCommand();
            deleteIntent.Transaction = tx;
            deleteIntent.CommandText = "DELETE FROM write_intents WHERE project_id = $project AND event_id = $event";
            deleteIntent.Parameters.AddWithValue("$project", item.ProjectId);
            deleteIntent.Parameters.AddWithValue("$event", item.EventId);
            deleteIntent.ExecuteNonQuery();
        }
        tx.Commit();
        return recovered;
    }

    public long NextSequence(string projectId)
    {
        using var command = sharedConnection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence_number), 0) + 1 FROM events WHERE project_id = $project";
        command.Parameters.AddWithValue("$project", projectId);
        return (long)command.ExecuteScalar()!;
    }

    public void Append(EventEnvelope evt)
    {
        var batchId = evt.BatchId ?? ("batch-" + evt.EventId);
        using var tx = sharedConnection.BeginTransaction();
        try
        {
            InsertIntent(evt, batchId, tx);
            InsertEvent(evt, tx);
            using var clear = sharedConnection.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM write_intents WHERE event_id = $event AND project_id = $project";
            clear.Parameters.AddWithValue("$event", evt.EventId);
            clear.Parameters.AddWithValue("$project", evt.ProjectId);
            clear.ExecuteNonQuery();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void LeaveWriteIntent(EventEnvelope evt)
    {
        using var tx = sharedConnection.BeginTransaction();
        InsertIntent(evt, evt.BatchId ?? ("batch-" + evt.EventId), tx);
        tx.Commit();
    }

    public void AppendWithoutCommitToSimulateCrash(EventEnvelope evt)
    {
        var tx = sharedConnection.BeginTransaction();
        InsertEvent(evt, tx);
        tx.Dispose();
    }

    public IReadOnlyList<EventEnvelope> List(string projectId)
    {
        var events = new List<EventEnvelope>();
        using var command = sharedConnection.CreateCommand();
        command.CommandText = """
            SELECT event_id, project_id, sequence_number, timestamp_text, event_type, branch,
                   parent_event_ids, rule_version, input_hash, payload_json, COALESCE(batch_id, '')
            FROM events WHERE project_id = $project ORDER BY sequence_number, event_id
        """;
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parents = reader.GetString(6).Split('|', StringSplitOptions.RemoveEmptyEntries);
            var batch = reader.GetString(10);
            events.Add(new EventEnvelope(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), parents, reader.GetString(7),
                reader.GetString(8), reader.GetString(9), string.IsNullOrEmpty(batch) ? null : batch));
        }
        return events;
    }

    public void ReplaceProject(string projectId, IReadOnlyList<EventEnvelope> events)
    {
        using var tx = sharedConnection.BeginTransaction();
        using (var delete = sharedConnection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM events WHERE project_id = $project";
            delete.Parameters.AddWithValue("$project", projectId);
            delete.ExecuteNonQuery();
        }
        foreach (var evt in events)
        {
            InsertIntent(evt, evt.BatchId ?? ("batch-" + evt.EventId), tx);
            InsertEvent(evt, tx);
            using var clearIntent = sharedConnection.CreateCommand();
            clearIntent.Transaction = tx;
            clearIntent.CommandText = "DELETE FROM write_intents WHERE event_id = $event AND project_id = $project";
            clearIntent.Parameters.AddWithValue("$event", evt.EventId);
            clearIntent.Parameters.AddWithValue("$project", projectId);
            clearIntent.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private void InsertEvent(EventEnvelope evt, SqliteTransaction tx)
    {
        using var command = sharedConnection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO events(event_id, project_id, sequence_number, timestamp_text, event_type, branch,
                parent_event_ids, rule_version, input_hash, payload_json, batch_id)
            VALUES ($event, $project, $sequence, $timestamp, $type, $branch, $parents, $rule, $hash, $payload, $batch)
        """;
        command.Parameters.AddWithValue("$event", evt.EventId);
        command.Parameters.AddWithValue("$project", evt.ProjectId);
        command.Parameters.AddWithValue("$sequence", evt.Sequence);
        command.Parameters.AddWithValue("$timestamp", evt.Timestamp);
        command.Parameters.AddWithValue("$type", evt.EventType);
        command.Parameters.AddWithValue("$branch", evt.Branch);
        command.Parameters.AddWithValue("$parents", string.Join('|', evt.ParentEventIds));
        command.Parameters.AddWithValue("$rule", evt.RuleVersion);
        command.Parameters.AddWithValue("$hash", evt.InputHash);
        command.Parameters.AddWithValue("$payload", evt.PayloadJson);
        command.Parameters.AddWithValue("$batch", (object?)evt.BatchId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void InsertIntent(EventEnvelope evt, string batchId, SqliteTransaction tx)
    {
        using var command = sharedConnection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO write_intents(event_id, project_id, sequence_number, batch_id, created_at)
            VALUES ($event, $project, $sequence, $batch, $at)
            ON CONFLICT(event_id) DO UPDATE SET project_id = excluded.project_id, sequence_number = excluded.sequence_number
        """;
        command.Parameters.AddWithValue("$event", evt.EventId);
        command.Parameters.AddWithValue("$project", evt.ProjectId);
        command.Parameters.AddWithValue("$sequence", evt.Sequence);
        command.Parameters.AddWithValue("$batch", batchId);
        command.Parameters.AddWithValue("$at", evt.Timestamp);
        command.ExecuteNonQuery();
    }

    public void Dispose() => sharedConnection.Dispose();
}
