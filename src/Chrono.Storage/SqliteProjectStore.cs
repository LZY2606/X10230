using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chrono.Core.Events;
using Chrono.Core.Model;
using Microsoft.Data.Sqlite;

namespace Chrono.Storage;

/// <summary>
/// SQLite event store. Writes go through a staging table inside one transaction: a crash midway
/// through an import leaves committed history untouched and the partial staging batch is discarded
/// on open (half-write recovery). Hash chains and dense per-branch sequences are verified on import.
/// </summary>
public sealed class SqliteProjectStore : IDisposable
{
    private readonly SqliteConnection _cn;
    private readonly object _gate = new();

    public SqliteProjectStore(string connectionString)
    {
        _cn = new SqliteConnection(connectionString);
        _cn.Open();
        using var pragma = _cn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        Schema();
        RecoverHalfWrittenStaging();
    }

    private void Schema()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS events(
  id TEXT PRIMARY KEY,
  branch TEXT NOT NULL,
  seq INTEGER NOT NULL,
  type TEXT NOT NULL,
  parent_event_id TEXT NULL,
  actor TEXT NOT NULL,
  created_at TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  input_summary TEXT NOT NULL,
  rule_version TEXT NOT NULL,
  hash TEXT NOT NULL,
  UNIQUE(branch, seq));
CREATE TABLE IF NOT EXISTS derivations(
  id TEXT PRIMARY KEY,
  branch TEXT NOT NULL,
  trigger_seq INTEGER NOT NULL,
  result_json TEXT NOT NULL,
  created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS staging(batch_id TEXT NOT NULL, seq_in_batch INTEGER NOT NULL, payload TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_events_branch ON events(branch, seq);
";
        cmd.ExecuteNonQuery();
    }

    /// <summary>A crashed import leaves rows only in staging; they are discarded, never half-applied.</summary>
    private void RecoverHalfWrittenStaging()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "DELETE FROM staging;";
        cmd.ExecuteNonQuery();
    }

    public bool HasProject()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type='project_created';";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public long NextSeq(string branch)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM events WHERE branch=$b;";
        cmd.Parameters.AddWithValue("$b", branch);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public EventRecord Append(EventRecord ev, DerivationResult? derivation = null)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();
            long expectedSeq = NextSeqLocked(ev.Branch, tx);
            if (ev.Seq != 0 && ev.Seq != expectedSeq)
                throw new InvalidOperationException(
                    $"branch {ev.Branch} concurrent append: expected seq {expectedSeq}, got {ev.Seq}");
            var toStore = ev.Seq == 0 ? ev with { Seq = expectedSeq } : ev;

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO events
(id,branch,seq,type,parent_event_id,actor,created_at,payload_json,input_summary,rule_version,hash)
VALUES($id,$b,$s,$t,$p,$a,$c,$j,$i,$r,$h);";
                Add(cmd, toStore);
                cmd.ExecuteNonQuery();
            }
            if (derivation is not null)
            {
                using var d = _cn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = @"INSERT OR REPLACE INTO derivations(id,branch,trigger_seq,result_json,created_at)
VALUES($id,$b,$s,$j,$c);";
                d.Parameters.AddWithValue("$id", derivation.DerivationId);
                d.Parameters.AddWithValue("$b", derivation.Branch);
                d.Parameters.AddWithValue("$s", derivation.TriggerSeq);
                d.Parameters.AddWithValue("$j", JsonSerializer.Serialize(derivation));
                d.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToString("O"));
                d.ExecuteNonQuery();
            }
            tx.Commit();
            return toStore;
        }
    }

    private long NextSeqLocked(string branch, SqliteTransaction tx)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events WHERE branch=$b;";
        cmd.Parameters.AddWithValue("$b", branch);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Add(SqliteCommand cmd, EventRecord ev)
    {
        cmd.Parameters.AddWithValue("$id", ev.Id);
        cmd.Parameters.AddWithValue("$b", ev.Branch);
        cmd.Parameters.AddWithValue("$s", ev.Seq);
        cmd.Parameters.AddWithValue("$t", ev.Type);
        cmd.Parameters.AddWithValue("$p", (object?)ev.ParentEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", ev.Actor);
        cmd.Parameters.AddWithValue("$c", ev.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$j", ev.PayloadJson);
        cmd.Parameters.AddWithValue("$i", ev.InputSummary);
        cmd.Parameters.AddWithValue("$r", ev.RuleVersion);
        cmd.Parameters.AddWithValue("$h", ev.Hash);
    }

    /// <summary>
    /// Staged batch import: all rows land in staging first, then a single transaction promotes them.
    /// If promoteCount differs from the batch size the caller treats it as a half-write and resets.
    /// </summary>
    public int StagedImport(IReadOnlyList<EventRecord> events, string batchId)
    {
        lock (_gate)
        {
            using (var clear = _cn.CreateCommand())
            {
                clear.CommandText = "DELETE FROM staging;";
                clear.ExecuteNonQuery();
            }
            using (var load = _cn.CreateCommand())
            {
                load.CommandText = "INSERT INTO staging(batch_id,seq_in_batch,payload) VALUES($b,$s,$p);";
                var pb = load.Parameters.Add("$b", SqliteType.Text);
                var ps = load.Parameters.Add("$s", SqliteType.Integer);
                var pp = load.Parameters.Add("$p", SqliteType.Text);
                foreach (var (ev, i) in events.Select((e, i) => (e, i)))
                {
                    pb.Value = batchId; ps.Value = i;
                    pp.Value = JsonSerializer.Serialize(ev);
                    load.ExecuteNonQuery();
                }
            }

            int promoted = 0;
            using (var tx = _cn.BeginTransaction())
            {
                try
                {
                    foreach (var ev in events)
                    {
                        using var cmd = _cn.CreateCommand();
                        cmd.Transaction = tx;
                        // dense per-branch sequence re-assigned in destination order; ids preserved
                        var toStore = ev with { Seq = NextSeqLocked(ev.Branch, tx) };
                        cmd.CommandText = @"INSERT OR REPLACE INTO events
(id,branch,seq,type,parent_event_id,actor,created_at,payload_json,input_summary,rule_version,hash)
VALUES($id,$b,$s,$t,$p,$a,$c,$j,$i,$r,$h);";
                        Add(cmd, toStore);
                        cmd.ExecuteNonQuery();
                        promoted++;
                    }
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    using var clear = _cn.CreateCommand();
                    clear.CommandText = "DELETE FROM staging;";
                    clear.ExecuteNonQuery();
                    throw;
                }
            }
            using (var clear = _cn.CreateCommand())
            {
                clear.CommandText = "DELETE FROM staging;";
                clear.ExecuteNonQuery();
            }
            return promoted;
        }
    }

    public IReadOnlyList<EventRecord> LoadEvents()
    {
        var list = new List<EventRecord>();
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT id,branch,seq,type,parent_event_id,actor,created_at,payload_json,input_summary,rule_version,hash FROM events ORDER BY branch,seq;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventRecord(
                r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                DateTimeOffset.Parse(r.GetString(6)), r.GetString(7), r.GetString(8), r.GetString(9))
            { Hash = r.GetString(10) });
        }
        return list;
    }

    public DerivationResult? LoadDerivation(string id)
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM derivations WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        var s = cmd.ExecuteScalar() as string;
        return s is null ? null : JsonSerializer.Deserialize<DerivationResult>(s);
    }

    public IReadOnlyList<DerivationResult> LoadDerivations()
    {
        var list = new List<DerivationResult>();
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM derivations;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(JsonSerializer.Deserialize<DerivationResult>(r.GetString(0))!);
        return list;
    }

    public void Wipe()
    {
        using var cmd = _cn.CreateCommand();
        cmd.CommandText = "DELETE FROM derivations; DELETE FROM events; DELETE FROM staging;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _cn.Dispose();
}
