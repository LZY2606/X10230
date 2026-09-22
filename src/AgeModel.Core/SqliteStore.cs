using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeModel.Core;

public sealed class ExportDoc
{
    public string FormatVersion { get; set; } = "agemodel-export/v1";
    public string ExportedAt { get; set; } = "";
    public List<StoredEventRow> Events { get; set; } = new();
    public List<StoredEntityRow> Entities { get; set; } = new();
    public List<StoredCandidateRow> Candidates { get; set; } = new();
    public List<KeyValuePair> Meta { get; set; } = new();
}

public sealed record StoredEventRow(long Seq, string Id, string Kind, string Payload, string At, string Actor, string BatchId, int Committed);
public sealed record StoredEntityRow(string Kind, string Id, string Json, string BatchId, long UpdatedSeq);
public sealed record StoredCandidateRow(string Id, string Json, string? FrozenBy, string? FrozenAt, string BatchId);
public sealed record KeyValuePair(string K, string V);

public sealed record FreezeOutcome(bool Won, string? FrozenBy, string? FrozenAt);

/// <summary>
/// SQLite-backed event/entity/candidate store.
/// Writes are batched and flagged committed; a torn half-write is recovered on open.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _conn;
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SqliteStore(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        Schema();
        RecoverHalfWrites();
    }

    public static SqliteStore OnFile(string path) =>
        new($"Data Source={path}");

    public SqliteConnection RawConnection => _conn;

    private void Schema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS batches(
  batch_id TEXT PRIMARY KEY, committed INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS events(
  seq INTEGER PRIMARY KEY, id TEXT NOT NULL, kind TEXT NOT NULL,
  payload TEXT NOT NULL, at TEXT NOT NULL, actor TEXT NOT NULL, batch_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS entities(
  kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL,
  batch_id TEXT NOT NULL, updated_seq INTEGER NOT NULL,
  PRIMARY KEY(kind, id));
CREATE TABLE IF NOT EXISTS candidates(
  id TEXT PRIMARY KEY, json TEXT NOT NULL,
  frozen_by TEXT, frozen_at TEXT, batch_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);");
    }

    /// <summary>Drop every row belonging to a batch that never reached committed=1 (half-write recovery).</summary>
    public int RecoverHalfWrites()
    {
        using var tx = _conn.BeginTransaction();
        var bad = new List<string>();
        using (var q = _conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT batch_id FROM batches WHERE committed=0";
            using var r = q.ExecuteReader();
            while (r.Read()) bad.Add(r.GetString(0));
        }
        foreach (var t in new[] { "events", "entities", "candidates" })
            Exec($"DELETE FROM {t} WHERE batch_id IN (SELECT batch_id FROM batches WHERE committed=0)", tx);
        Exec("DELETE FROM batches WHERE committed=0", tx);
        tx.Commit();
        return bad.Count;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var q = _conn.CreateCommand();
        q.CommandText = sql;
        if (tx != null) q.Transaction = tx;
        q.ExecuteNonQuery();
    }

    public sealed record BatchWrite(
        string BatchId,
        IReadOnlyList<DomainEvent> Events,
        IReadOnlyList<(string Kind, string Id, string Json, long UpdatedSeq)> Entities,
        IReadOnlyList<ModelCandidate> Candidates);

    /// <summary>
    /// Atomic batch: rows only become visible together once committed=1.
    /// Explicit event seqs are preserved (stable identity).
    /// </summary>
    public void AppendBatch(BatchWrite w)
    {
        using var tx = _conn.BeginTransaction();
        InsertBatchMarker(w.BatchId, committed: 0, tx);
        foreach (var e in w.Events) InsertEvent(e, tx);
        foreach (var (kind, id, json, updatedSeq) in w.Entities)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = @"INSERT INTO entities(kind,id,json,batch_id,updated_seq)
                              VALUES($k,$id,$j,$b,$s)
                              ON CONFLICT(kind,id) DO UPDATE SET
                                json=excluded.json, batch_id=excluded.batch_id, updated_seq=excluded.updated_seq";
            Add(q, "$k", kind); Add(q, "$id", id); Add(q, "$j", json);
            Add(q, "$b", w.BatchId); Add(q, "$s", updatedSeq);
            q.ExecuteNonQuery();
        }
        foreach (var c in w.Candidates)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO candidates(id,json,frozen_by,frozen_at,batch_id) VALUES($id,$j,NULL,NULL,$b)";
            Add(q, "$id", c.Id); Add(q, "$j", JsonSerializer.Serialize(c, JsonOpts)); Add(q, "$b", w.BatchId);
            q.ExecuteNonQuery();
        }
        SetCommitted(w.BatchId, 1, tx);
        tx.Commit();
    }

    private void InsertBatchMarker(string batchId, int committed, SqliteTransaction tx)
    {
        using var q = _conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT OR REPLACE INTO batches(batch_id,committed,created_at) VALUES($b,$c,$t)";
        Add(q, "$b", batchId); Add(q, "$c", committed);
        Add(q, "$t", DateTime.UtcNow.ToString("o"));
        q.ExecuteNonQuery();
    }

    private void SetCommitted(string batchId, int committed, SqliteTransaction tx)
    {
        using var q = _conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "UPDATE batches SET committed=$c WHERE batch_id=$b";
        Add(q, "$c", committed); Add(q, "$b", batchId);
        q.ExecuteNonQuery();
    }

    private void InsertEvent(DomainEvent e, SqliteTransaction tx)
    {
        using var q = _conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = @"INSERT INTO events(seq,id,kind,payload,at,actor,batch_id)
                          VALUES($seq,$id,$k,$p,$at,$ac,$b)";
        Add(q, "$seq", e.Seq); Add(q, "$id", e.Id); Add(q, "$k", e.Kind);
        Add(q, "$p", e.PayloadJson); Add(q, "$at", e.At); Add(q, "$ac", e.Actor);
        Add(q, "$b", e.BatchId);
        q.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand q, string name, object value)
    {
        var p = q.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        q.Parameters.Add(p);
    }

    public long LastSeq()
    {
        using var q = _conn.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
        return (long)(q.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Test-only fault injection: commit rows while leaving committed=0,
    /// simulating a torn half-write. Next RecoverHalfWrites must remove them.
    /// </summary>
    public void SimulateTornBatch(BatchWrite w)
    {
        using var tx = _conn.BeginTransaction();
        InsertBatchMarker(w.BatchId, committed: 0, tx);
        foreach (var e in w.Events) InsertEvent(e, tx);
        foreach (var c in w.Candidates)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO candidates(id,json,frozen_by,frozen_at,batch_id) VALUES($id,$j,NULL,NULL,$b)";
            Add(q, "$id", c.Id); Add(q, "$j", JsonSerializer.Serialize(c, JsonOpts)); Add(q, "$b", w.BatchId);
            q.ExecuteNonQuery();
        }
        // deliberately no SetCommitted(...,1,...)
        tx.Commit();
    }

    // ---- reads -------------------------------------------------------------

    public List<StoredEntityRow> ReadEntities(string? kind = null)
    {
        var rows = new List<StoredEntityRow>();
        using var q = _conn.CreateCommand();
        q.CommandText = kind is null
            ? "SELECT kind,id,json,batch_id,updated_seq FROM entities ORDER BY updated_seq,kind,id"
            : "SELECT kind,id,json,batch_id,updated_seq FROM entities WHERE kind=$k ORDER BY updated_seq,id";
        if (kind != null) Add(q, "$k", kind);
        using var r = q.ExecuteReader();
        while (r.Read())
            rows.Add(new StoredEntityRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetInt64(4)));
        return rows;
    }

    public List<DomainEvent> ReadEvents(long? upToSeq = null)
    {
        var rows = new List<DomainEvent>();
        using var q = _conn.CreateCommand();
        q.CommandText = upToSeq is null
            ? "SELECT seq,id,kind,payload,at,actor,batch_id FROM events ORDER BY seq"
            : "SELECT seq,id,kind,payload,at,actor,batch_id FROM events WHERE seq<=$s ORDER BY seq";
        if (upToSeq != null) Add(q, "$s", upToSeq.Value);
        using var r = q.ExecuteReader();
        while (r.Read())
            rows.Add(new DomainEvent(r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return rows;
    }

    public List<ModelCandidate> ReadCandidates(bool includeFrozenState = true)
    {
        var rows = new List<(ModelCandidate C, string? By, string? At)>();
        using var q = _conn.CreateCommand();
        q.CommandText = "SELECT json,frozen_by,frozen_at FROM candidates ORDER BY id";
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            var c = JsonSerializer.Deserialize<ModelCandidate>(r.GetString(0), JsonOpts)!;
            var by = r.IsDBNull(1) ? null : r.GetString(1);
            var at = r.IsDBNull(2) ? null : r.GetString(2);
            rows.Add((includeFrozenState ? c with { FrozenBy = by, FrozenAt = at } : c, by, at));
        }
        return rows.Select(x => x.C).ToList();
    }

    public List<StoredCandidateRow> ReadCandidateRows()
    {
        var rows = new List<StoredCandidateRow>();
        using var q = _conn.CreateCommand();
        q.CommandText = "SELECT id,json,frozen_by,frozen_at,batch_id FROM candidates ORDER BY id";
        using var r = q.ExecuteReader();
        while (r.Read())
            rows.Add(new StoredCandidateRow(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        return rows;
    }

    /// <summary>Atomic freeze: exactly one concurrent caller wins.</summary>
    public FreezeOutcome Freeze(string candidateId, string actor)
    {
        var at = DateTime.UtcNow.ToString("o");
        using var tx = _conn.BeginTransaction();
        using var q = _conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = @"UPDATE candidates SET frozen_by=$a, frozen_at=$t
                          WHERE id=$id AND frozen_by IS NULL";
        Add(q, "$a", actor); Add(q, "$t", at); Add(q, "$id", candidateId);
        var won = q.ExecuteNonQuery() == 1;
        if (won)
        {
            var seq = LastSeq() + 1;
            var ev = new DomainEvent(seq, $"ev-{seq:D6}", Ev.ModelFrozen,
                JsonSerializer.Serialize(new { candidateId, actor, at }, JsonOpts),
                at, actor, $"freeze-{candidateId}");
            InsertEvent(ev, tx);
            tx.Commit();
            return new FreezeOutcome(true, actor, at);
        }
        tx.Rollback();
        using var q2 = _conn.CreateCommand();
        q2.CommandText = "SELECT frozen_by, frozen_at FROM candidates WHERE id=$id";
        Add(q2, "$id", candidateId);
        using var r = q2.ExecuteReader();
        return r.Read()
            ? new FreezeOutcome(false, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1))
            : throw new KeyNotFoundException(candidateId);
    }

    public void AppendConflictEvent(string candidateId, string actor)
    {
        var seq = LastSeq() + 1;
        var at = DateTime.UtcNow.ToString("o");
        var ev = new DomainEvent(seq, $"ev-{seq:D6}", Ev.FreezeConflict,
            JsonSerializer.Serialize(new { candidateId, actor, at }, JsonOpts), at, actor,
            $"freeze-conflict-{candidateId}");
        using var tx = _conn.BeginTransaction();
        InsertBatchMarker(ev.BatchId, 0, tx);
        InsertEvent(ev, tx);
        SetCommitted(ev.BatchId, 1, tx);
        tx.Commit();
    }

    // ---- export / import ---------------------------------------------------

    public ExportDoc Export()
    {
        var doc = new ExportDoc { ExportedAt = DateTime.UtcNow.ToString("o") };
        doc.Events = ReadEvents().Select(e => new StoredEventRow(e.Seq, e.Id, e.Kind, e.PayloadJson, e.At, e.Actor, e.BatchId, 1)).ToList();
        doc.Entities = ReadEntities();
        doc.Candidates = ReadCandidateRows();
        using var q = _conn.CreateCommand();
        q.CommandText = "SELECT k,v FROM meta ORDER BY k";
        using var r = q.ExecuteReader();
        while (r.Read()) doc.Meta.Add(new KeyValuePair(r.GetString(0), r.GetString(1)));
        return doc;
    }

    /// <summary>Import into an empty store; ids, seq order and provenance are preserved.</summary>
    public void Import(ExportDoc doc)
    {
        if (LastSeq() != 0 || ReadEntities().Count != 0)
            throw new InvalidOperationException("导入目标库必须为空，避免身份冲突");
        using var tx = _conn.BeginTransaction();
        foreach (var b in doc.Events.Select(e => e.BatchId).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            using var mq = _conn.CreateCommand();
            mq.Transaction = tx;
            mq.CommandText = "INSERT OR REPLACE INTO batches(batch_id,committed,created_at) VALUES($b,1,$t)";
            Add(mq, "$b", b); Add(mq, "$t", DateTime.UtcNow.ToString("o"));
            mq.ExecuteNonQuery();
        }
        foreach (var e in doc.Events.OrderBy(e => e.Seq))
            InsertEvent(new DomainEvent(e.Seq, e.Id, e.Kind, e.Payload, e.At, e.Actor, e.BatchId), tx);
        foreach (var en in doc.Entities)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT INTO entities(kind,id,json,batch_id,updated_seq) VALUES($k,$id,$j,$b,$s)";
            Add(q, "$k", en.Kind); Add(q, "$id", en.Id); Add(q, "$j", en.Json);
            Add(q, "$b", en.BatchId); Add(q, "$s", en.UpdatedSeq);
            q.ExecuteNonQuery();
        }
        foreach (var c in doc.Candidates)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT INTO candidates(id,json,frozen_by,frozen_at,batch_id) VALUES($id,$j,$fb,$fa,$b)";
            Add(q, "$id", c.Id); Add(q, "$j", c.Json);
            Add(q, "$fb", (object?)c.FrozenBy ?? DBNull.Value);
            Add(q, "$fa", (object?)c.FrozenAt ?? DBNull.Value);
            Add(q, "$b", c.BatchId);
            q.ExecuteNonQuery();
        }
        foreach (var m in doc.Meta)
        {
            using var q = _conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v)";
            Add(q, "$k", m.K); Add(q, "$v", m.V);
            q.ExecuteNonQuery();
        }
        var seq = LastSeq() + 1;
        var ev = new DomainEvent(seq, $"ev-{seq:D6}", Ev.ImportCompleted,
            JsonSerializer.Serialize(new { doc.FormatVersion, events = doc.Events.Count, entities = doc.Entities.Count }, JsonOpts),
            DateTime.UtcNow.ToString("o"), "importer", $"import-{Guid.NewGuid():N}");
        InsertBatchMarker(ev.BatchId, 0, tx);
        InsertEvent(ev, tx);
        SetCommitted(ev.BatchId, 1, tx);
        tx.Commit();
    }

    public void Dispose() => _conn.Dispose();
}
