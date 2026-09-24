using System.Data;
using Chrono.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Chrono.Storage;

/// <summary>
/// SQLite-backed immutable event log. Concurrency uses an optimistic last-event
/// token. Half-written rows are quarantined on open rather than silently dropped,
/// so a crashed commit becomes a distinct recoverable state.
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private string? _armedCrashMarker;

    public SqliteEventStore(string dbPath)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true
        };
        _connectionString = cs.ToString();
    }

    public SqliteConnection OpenRaw() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE IF NOT EXISTS events_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events(
                    seq INTEGER PRIMARY KEY,
                    event_id TEXT NOT NULL UNIQUE,
                    branch_id TEXT NOT NULL,
                    parent_event_id TEXT,
                    rule_version TEXT NOT NULL,
                    input_digest TEXT NOT NULL,
                    type TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    source_fragment TEXT NOT NULL,
                    author TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    committed INTEGER NOT NULL DEFAULT 1,
                    complete INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS ix_events_branch ON events(branch_id, seq);
                CREATE TABLE IF NOT EXISTS quarantine(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    payload TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    detected_at TEXT NOT NULL
                );
                """;
            ddl.ExecuteNonQuery();
        }
        RecoverHalfWrites(conn);
    }

    private static void RecoverHalfWrites(SqliteConnection conn)
    {
        List<(long seq, string payload)> rows = new();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT seq, payload_json FROM events WHERE committed = 0 OR complete = 0";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (seq, payload) in rows)
        {
            using var tx = conn.BeginTransaction();
            using (var move = conn.CreateCommand())
            {
                move.Transaction = tx;
                move.CommandText = """
                    INSERT INTO quarantine(payload, reason, detected_at)
                    VALUES($p, $r, $t);
                    DELETE FROM events WHERE seq = $s;
                    """;
                move.Parameters.AddWithValue("$p", payload);
                move.Parameters.AddWithValue("$r", "half-write: commit marker missing / payload truncated");
                move.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                move.Parameters.AddWithValue("$s", seq);
                move.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ArmHalfWriteCrash(string marker) => _armedCrashMarker = marker;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct);
        return c;
    }

    public async Task<IReadOnlyList<StoredEvent>> LoadAllAsync(CancellationToken ct = default)
    {
        using var conn = await OpenAsync(ct);
        RecoverHalfWrites(conn);
        var list = new List<StoredEvent>();
        using var q = conn.CreateCommand();
        q.CommandText = """
            SELECT event_id, seq, branch_id, parent_event_id, rule_version, input_digest,
                   type, payload_json, source_fragment, author, occurred_at
            FROM events WHERE committed = 1 AND complete = 1 ORDER BY seq
            """;
        using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new StoredEvent
            {
                EventId = r.GetString(0), Seq = r.GetInt64(1), BranchId = r.GetString(2),
                ParentEventId = r.IsDBNull(3) ? null : r.GetString(3),
                RuleVersion = r.GetString(4), InputDigest = r.GetString(5),
                Type = Enum.Parse<EventType>(r.GetString(6)),
                PayloadJson = r.GetString(7), SourceFragment = r.GetString(8),
                Author = r.GetString(9), OccurredAt = r.GetString(10)
            });
        }
        return list;
    }

    public Task<IReadOnlyList<string>> QuarantineAsync()
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var list = new List<string>();
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT reason FROM quarantine";
            using var r = q.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return Task.FromResult<IReadOnlyList<string>>(list);
        }
    }

    public async Task<AppendResult> AppendAsync(StoredEvent e, string? expectedLastEventId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            string? actualLast = null;
            long nextSeq = 1;
            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = "SELECT event_id, COALESCE(MAX(seq),0) FROM events WHERE committed=1 AND complete=1";
                using var r = head.ExecuteReader();
                if (r.Read()) { actualLast = r.IsDBNull(0) ? null : r.GetString(0); nextSeq = r.GetInt64(1) + 1; }
            }
            if (!string.Equals(actualLast, expectedLastEventId, StringComparison.Ordinal))
            {
                tx.Rollback();
                return new AppendResult(false, actualLast, null);
            }

            var crash = _armedCrashMarker;
            _armedCrashMarker = null;
            var (committed, complete, payload) = crash is null
                ? (1, 1, e.PayloadJson)
                : (0, 0, e.PayloadJson.Length > 40 ? e.PayloadJson[..40] : e.PayloadJson);

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO events(seq,event_id,branch_id,parent_event_id,rule_version,input_digest,
                    type,payload_json,source_fragment,author,occurred_at,committed,complete)
                VALUES($s,$id,$b,$p,$rv,$d,$t,$pj,$sf,$a,$o,$c,$cm)
                """;
            ins.Parameters.AddWithValue("$s", nextSeq);
            ins.Parameters.AddWithValue("$id", e.EventId);
            ins.Parameters.AddWithValue("$b", e.BranchId);
            ins.Parameters.AddWithValue("$p", (object?)e.ParentEventId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$rv", e.RuleVersion);
            ins.Parameters.AddWithValue("$d", e.InputDigest);
            ins.Parameters.AddWithValue("$t", e.Type.ToString());
            ins.Parameters.AddWithValue("$pj", payload);
            ins.Parameters.AddWithValue("$sf", e.SourceFragment);
            ins.Parameters.AddWithValue("$a", e.Author);
            ins.Parameters.AddWithValue("$o", e.OccurredAt);
            ins.Parameters.AddWithValue("$c", committed);
            ins.Parameters.AddWithValue("$cm", complete);
            ins.ExecuteNonQuery();

            if (crash is null) tx.Commit();
            else return new AppendResult(false, actualLast, null); // simulated crash: never commit

            return new AppendResult(true, null, e with { Seq = nextSeq });
        }
    }
}
