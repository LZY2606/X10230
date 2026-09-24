using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeModel;

/// <summary>
/// SQLite 事件溯源存储。材料层 append-only（版本化），事件只增不改，
/// 模型派生记录不可变，冻结通过 freezes 表的唯一约束实现并发互斥。
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;

    public Store(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate }.ToString();
        _db = new SqliteConnection(cs);
        _db.Open();
        if (path == ":memory:") { /* keep single connection */ }
        Exec(@"PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS materials(
                id TEXT NOT NULL, version INT NOT NULL, kind TEXT NOT NULL,
                json TEXT NOT NULL, raw TEXT NOT NULL, created_seq INT NOT NULL,
                PRIMARY KEY(id, version));");
        Exec(@"CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL, kind TEXT NOT NULL,
                payload TEXT NOT NULL, input_hash TEXT NOT NULL, param_version TEXT NOT NULL,
                parents TEXT NOT NULL, created_at TEXT NOT NULL);");
        Exec(@"CREATE TABLE IF NOT EXISTS models(
                id TEXT PRIMARY KEY, core_id TEXT NOT NULL, segment_index INT NOT NULL,
                candidate_index INT NOT NULL, json TEXT NOT NULL, depends_on TEXT NOT NULL,
                param_version TEXT NOT NULL, derived_seq INT NOT NULL);");
        Exec(@"CREATE TABLE IF NOT EXISTS freezes(model_id TEXT PRIMARY KEY, seq INT NOT NULL, by TEXT NOT NULL);");
        Exec(@"CREATE TABLE IF NOT EXISTS import_journal(id TEXT PRIMARY KEY, state TEXT NOT NULL, detail TEXT);");
        Exec(@"CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }

    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    public long NextSeqPeek()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    public AppEvent AppendEvent(string kind, object payload, List<string>? parents = null, string? paramVersion = null)
    {
        var json = Json.Ser(payload);
        var id = $"evt-{Hashing.Of(json + Guid.NewGuid())[..12]}";
        var parentsJson = Json.Ser(parents ?? new());
        var pv = paramVersion ?? GetParamVersion();
        var now = DateTimeOffset.UtcNow.ToString("o");
        Exec("INSERT INTO events(id,kind,payload,input_hash,param_version,parents,created_at) VALUES($i,$k,$p,$h,$pv,$pa,$t)",
            ("$i", id), ("$k", kind), ("$p", json), ("$h", Hashing.Of(json)), ("$pv", pv), ("$pa", parentsJson), ("$t", now));
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT seq FROM events WHERE id=$i";
        c.Parameters.AddWithValue("$i", id);
        var seq = (long)(c.ExecuteScalar() ?? 0L);
        return new AppEvent { Seq = seq, Id = id, Kind = kind, Payload = JsonDocument.Parse(json).RootElement.Clone(), InputHash = Hashing.Of(json), ParamVersion = pv, Parents = parents ?? new(), CreatedAt = now };
    }

    public void AddMaterial(Material m, long atSeq)
    {
        Exec("INSERT INTO materials(id,version,kind,json,raw,created_seq) VALUES($i,$v,$k,$j,$r,$s)",
            ("$i", m.Id), ("$v", m.Version), ("$k", m.Kind), ("$j", Json.Ser((object)m)), ("$r", m.Raw), ("$s", atSeq));
    }

    public bool MaterialExists(string id, int version)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(1) FROM materials WHERE id=$i AND version=$v";
        c.Parameters.AddWithValue("$i", id); c.Parameters.AddWithValue("$v", version);
        return (long)(c.ExecuteScalar() ?? 0L) > 0;
    }

    /// <summary>截至 seq 可见的每份材料的最新版本（时间回溯的基础）。</summary>
    public List<Material> MaterialsAsOf(long seq)
    {
        using var c = _db.CreateCommand();
        c.CommandText = @"SELECT m.kind, m.json FROM materials m
            JOIN (SELECT id, MAX(version) v FROM materials WHERE created_seq<=$s GROUP BY id) latest
              ON latest.id=m.id AND latest.v=m.version
            WHERE m.created_seq<=$s ORDER BY m.id";
        c.Parameters.AddWithValue("$s", seq);
        var list = new List<Material>();
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var kind = r.GetString(0); var json = r.GetString(1);
            list.Add(DeserializeMaterial(kind, json));
        }
        return list;
    }

    public static Material DeserializeMaterial(string kind, string json) => kind switch
    {
        "depth-datum" => Json.De<DepthDatum>(json)!,
        "segment-gap" => Json.De<SegmentGap>(json)!,
        "radio-age" => Json.De<RadioAge>(json)!,
        "reservoir-offset" => Json.De<ReservoirOffset>(json)!,
        "event-layer" => Json.De<EventLayer>(json)!,
        "exclusion" => Json.De<ExclusionRecord>(json)!,
        _ => throw new InvalidDataException($"unknown material kind {kind}")
    };

    public List<AppEvent> EventsAsOf(long seq)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT seq,id,kind,payload,input_hash,param_version,parents,created_at FROM events WHERE seq<=$s ORDER BY seq";
        c.Parameters.AddWithValue("$s", seq);
        var list = new List<AppEvent>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new AppEvent
            {
                Seq = r.GetInt64(0), Id = r.GetString(1), Kind = r.GetString(2),
                Payload = JsonDocument.Parse(r.GetString(3)).RootElement.Clone(),
                InputHash = r.GetString(4), ParamVersion = r.GetString(5),
                Parents = Json.De<List<string>>(r.GetString(6)) ?? new(), CreatedAt = r.GetString(7)
            });
        return list;
    }

    public void SaveModel(ModelCandidate m) =>
        Exec("INSERT OR REPLACE INTO models(id,core_id,segment_index,candidate_index,json,depends_on,param_version,derived_seq) VALUES($i,$c,$s,$ci,$j,$d,$pv,$ds)",
            ("$i", m.Id), ("$c", m.CoreId), ("$s", m.SegmentIndex), ("$ci", m.CandidateIndex),
            ("$j", Json.Ser(m)), ("$d", Json.Ser(m.DependsOn)), ("$pv", m.ParamVersion), ("$ds", m.DerivedAtSeq));

    public List<ModelCandidate> ModelsDerivedAsOf(long seq)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT json FROM models WHERE derived_seq<=$s ORDER BY core_id, segment_index, candidate_index, derived_seq";
        c.Parameters.AddWithValue("$s", seq);
        var list = new List<ModelCandidate>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(Json.De<ModelCandidate>(r.GetString(0))!);
        // 状态由事件回放重建：superseded = 同 (core,segment,candidate) 有更新派生；frozen = 有冻结记录
        var frozen = FrozenAsOf(seq);
        return list
            .GroupBy(m => (m.CoreId, m.SegmentIndex, m.CandidateIndex))
            .Select(g => g.OrderByDescending(m => m.DerivedAtSeq).First() with
            {
                Status = frozen.Contains(g.OrderByDescending(m => m.DerivedAtSeq).First().Id) ? "frozen" :
                         g.Count() > 1 ? "superseded" : "active"
            })
            .OrderBy(m => m.CoreId).ThenBy(m => m.SegmentIndex).ThenBy(m => m.CandidateIndex)
            .ToList();
    }

    public List<object> FreezeRowsAsOf(long seq)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT model_id,seq,by FROM freezes WHERE seq<=$s ORDER BY model_id";
        c.Parameters.AddWithValue("$s", seq);
        var list = new List<object>();
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new { modelId = r.GetString(0), seq = r.GetInt64(1), by = r.GetString(2) });
        return list;
    }

    public HashSet<string> FrozenAsOf(long seq)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT model_id FROM freezes WHERE seq<=$s";
        c.Parameters.AddWithValue("$s", seq);
        var set = new HashSet<string>();
        using var r = c.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>并发冻结：唯一约束保证同一模型只有一个冻结获胜者。</summary>
    public Outcome<string> Freeze(string modelId, string by)
    {
        var seq = NextSeqPeek() + 1;
        try
        {
            Exec("INSERT INTO freezes(model_id,seq,by) VALUES($m,$s,$b)", ("$m", modelId), ("$s", seq), ("$b", by));
            return Outcome<string>.Ok(modelId);
        }
        catch (SqliteException)
        {
            return Outcome<string>.Fail(OutcomeStatus.Conflict,
                new Diagnostic("freeze-conflict", $"model {modelId} already frozen", modelId, null, "error"));
        }
    }

    public string GetParamVersion()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT value FROM meta WHERE key='paramVersion'";
        return c.ExecuteScalar() as string ?? ParamSet.V1.Version;
    }

    public void SetParamVersion(string v) =>
        Exec("INSERT OR REPLACE INTO meta(key,value) VALUES('paramVersion',$v)", ("$v", v));

    // ---------- 导出 / 导入（往返保持身份、顺序、来源关系） ----------
    public string ExportArchive()
    {
        var materials = new List<object>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT json,created_seq FROM materials ORDER BY id,version";
            using var r = c.ExecuteReader();
            while (r.Read()) materials.Add(new { createdSeq = r.GetInt64(1), material = JsonDocument.Parse(r.GetString(0)).RootElement.Clone() });
        }
        var archive = new
        {
            format = "agemodel-archive",
            formatVersion = 1,
            exportedAt = DateTimeOffset.UtcNow.ToString("o"),
            paramVersion = GetParamVersion(),
            materials,
            events = EventsAsOf(long.MaxValue).Select(e => new { e.Seq, e.Id, e.Kind, payload = e.Payload, e.InputHash, e.ParamVersion, e.Parents, e.CreatedAt }).ToList(),
            models = ModelsDerivedAsOf(long.MaxValue),
            freezes = FreezeRowsAsOf(long.MaxValue)
        };
        return Json.Ser(archive);
    }

    /// <summary>事务化导入；faultAfterN 用于半写恢复测试（注入故障）。</summary>
    public Outcome<string> ImportArchive(string archiveJson, int? faultAfterN = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(archiveJson); }
        catch (JsonException)
        {
            return Outcome<string>.Fail(OutcomeStatus.FormatCorrupt,
                new Diagnostic("format-corrupt", "archive is not valid JSON", null, null, "error"));
        }
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var f) || f.GetString() != "agemodel-archive")
            return Outcome<string>.Fail(OutcomeStatus.FormatCorrupt,
                new Diagnostic("format-corrupt", "archive format marker missing", null, null, "error"));

        var journalId = $"import-{Guid.NewGuid():N}";
        Exec("INSERT INTO import_journal(id,state,detail) VALUES($i,'importing','')", ("$i", journalId));
        var inserted = 0;
        try
        {
            using var tx = _db.BeginTransaction();
            void Step(Action a) { a(); if (faultAfterN is int n && ++inserted >= n) throw new FaultInjected(); }
            foreach (var mEl in root.GetProperty("materials").EnumerateArray())
            {
                var mNode = mEl.GetProperty("material");
                var createdSeq = mEl.GetProperty("createdSeq").GetInt64();
                var kind = mNode.GetProperty("kind").GetString()!;
                var m = DeserializeMaterial(kind, mNode.GetRawText());
                Step(() => { if (!MaterialExists(m.Id, m.Version)) AddMaterial(m, createdSeq); });
            }
            foreach (var eEl in root.GetProperty("events").EnumerateArray())
            {
                var id = eEl.GetProperty("id").GetString()!;
                Step(() => Exec("INSERT OR IGNORE INTO events(seq,id,kind,payload,input_hash,param_version,parents,created_at) VALUES($s,$i,$k,$p,$h,$pv,$pa,$t)",
                    ("$s", eEl.GetProperty("seq").GetInt64()), ("$i", id), ("$k", eEl.GetProperty("kind").GetString()!),
                    ("$p", eEl.GetProperty("payload").GetRawText()), ("$h", eEl.GetProperty("inputHash").GetString()!),
                    ("$pv", eEl.GetProperty("paramVersion").GetString()!), ("$pa", eEl.GetProperty("parents").GetRawText()),
                    ("$t", eEl.GetProperty("createdAt").GetString()!)));
            }
            foreach (var mEl in root.GetProperty("models").EnumerateArray())
            {
                var m = Json.De<ModelCandidate>(mEl.GetRawText())!;
                Step(() => SaveModel(m));
            }
            foreach (var fr in root.GetProperty("freezes").EnumerateArray())
            {
                var mid = fr.GetProperty("modelId").GetString()!;
                var fseq = fr.GetProperty("seq").GetInt64();
                var fby = fr.GetProperty("by").GetString()!;
                Step(() => Exec("INSERT OR IGNORE INTO freezes(model_id,seq,by) VALUES($m,$s,$b)", ("$m", mid), ("$s", fseq), ("$b", fby)));
            }
            if (root.TryGetProperty("paramVersion", out var pv)) SetParamVersion(pv.GetString()!);
            tx.Commit();
            Exec("UPDATE import_journal SET state='done' WHERE id=$i", ("$i", journalId));
            return Outcome<string>.Ok(journalId);
        }
        catch (Exception e) when (e is FaultInjected or SqliteException or InvalidDataException)
        {
            // 事务回滚，留下未完成日志，等待 Recover()
            return e is FaultInjected
                ? Outcome<string>.Fail(OutcomeStatus.FormatCorrupt, new Diagnostic("import-interrupted", "import aborted mid-write; run Recover()", null, null, "error"))
                : Outcome<string>.Fail(OutcomeStatus.FormatCorrupt, new Diagnostic("import-failed", e.Message, null, null, "error"));
        }
    }

    public sealed class FaultInjected : Exception { }

    /// <summary>半写恢复：清除未完成的导入日志；事务保证数据本体无半成品。</summary>
    public int Recover()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "DELETE FROM import_journal WHERE state='importing'";
        return c.ExecuteNonQuery();
    }

    public int PendingImports()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(1) FROM import_journal WHERE state='importing'";
        return (int)(long)(c.ExecuteScalar() ?? 0L);
    }

    public void Dispose() => _db.Dispose();
}
