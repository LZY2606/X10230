using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgeDepth.Core;

/// <summary>
/// SQLite 事件存储。追加需带期望序列号（并发冻结检验）；
/// 导入走 staging 标记，崩溃后恢复会丢弃半写数据。
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string? _ownedMemoryName;

    private EventStore(SqliteConnection conn, string? ownedMemoryName = null)
    { _conn = conn; _ownedMemoryName = ownedMemoryName; Init(); }

    public static EventStore Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return new EventStore(conn);
    }

    /// <summary>进程内共享内存库（测试用），连接保持打开。</summary>
    public static EventStore InMemory()
    {
        var name = $"agedepth-{Guid.NewGuid():N}";
        var conn = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
        conn.Open();
        return new EventStore(conn, name);
    }

    private void Init()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
              seq INTEGER PRIMARY KEY,
              id TEXT NOT NULL,
              kind TEXT NOT NULL,
              at TEXT NOT NULL,
              param_version TEXT NOT NULL,
              input_hash TEXT NOT NULL,
              parents TEXT NOT NULL,
              payload TEXT NOT NULL,
              import_id TEXT);
            CREATE INDEX IF NOT EXISTS idx_events_kind ON events(kind);
            CREATE TABLE IF NOT EXISTS imports(
              id TEXT PRIMARY KEY,
              status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS meta(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        Recover();
    }

    public long HeadSeq
    {
        get
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM events";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public EventRecord Append(string kind, string payload, string[] parents,
        string paramVersion, string inputHash, long? expectedSeq = null, string? importId = null)
    {
        using var tx = _conn.BeginTransaction();
        var head = HeadSeq;
        if (expectedSeq is { } exp && exp != head)
            throw new ConcurrencyConflictException(exp, head);
        var rec = new EventRecord(head + 1, $"evt-{head + 1:D6}", kind,
            DateTimeOffset.UtcNow.ToString("o"), paramVersion, inputHash, parents, payload);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events(seq,id,kind,at,param_version,input_hash,parents,payload,import_id)
                VALUES($seq,$id,$kind,$at,$pv,$ih,$parents,$payload,$importId)
                """;
            cmd.Parameters.AddWithValue("$seq", rec.Seq);
            cmd.Parameters.AddWithValue("$id", rec.Id);
            cmd.Parameters.AddWithValue("$kind", rec.Kind);
            cmd.Parameters.AddWithValue("$at", rec.At);
            cmd.Parameters.AddWithValue("$pv", rec.ParamVersion);
            cmd.Parameters.AddWithValue("$ih", rec.InputHash);
            cmd.Parameters.AddWithValue("$parents", JsonSerializer.Serialize(rec.Parents));
            cmd.Parameters.AddWithValue("$payload", rec.Payload);
            cmd.Parameters.AddWithValue("$importId", (object?)importId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return rec;
    }

    public IReadOnlyList<EventRecord> Read(long? upTo = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = upTo is null
            ? "SELECT seq,id,kind,at,param_version,input_hash,parents,payload FROM events ORDER BY seq"
            : "SELECT seq,id,kind,at,param_version,input_hash,parents,payload FROM events WHERE seq<=$upTo ORDER BY seq";
        if (upTo is { } u) cmd.Parameters.AddWithValue("$upTo", u);
        var list = new List<EventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EventRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5),
                JsonSerializer.Deserialize<string[]>(r.GetString(6)) ?? Array.Empty<string>(),
                r.GetString(7)));
        return list;
    }

    // ---- 导入 staging：半写恢复 ----

    public string BeginImport()
    {
        var id = $"imp-{Guid.NewGuid():N}";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO imports(id,status) VALUES($id,'started')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return id;
    }

    public void CommitImport(string importId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE imports SET status='committed' WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", importId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>恢复：丢弃所有未提交导入写入的事件（半写数据）。返回清理条数。</summary>
    public int Recover()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM events WHERE import_id IN (SELECT id FROM imports WHERE status='started');
            UPDATE imports SET status='recovered' WHERE status='started';
            SELECT changes();
            """;
        using var r = cmd.ExecuteReader();
        return r.Read() ? r.GetInt32(0) : 0;
    }

    // ---- 导出 / 导入 ----

    public string ExportJson()
    {
        var events = Read().Select(e => new
        {
            e.Seq, e.Id, e.Kind, e.At, e.ParamVersion, e.InputHash, e.Parents,
            Payload = JsonSerializer.Deserialize<JsonElement>(e.Payload)
        });
        var bundle = new
        {
            format = "agedepth-export/1",
            exportedAt = DateTimeOffset.UtcNow.ToString("o"),
            events
        };
        return JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>导入导出包：单事务 + staging 标记；保留原始 seq（顺序）、id（身份）、parents（来源）。</summary>
    public void ImportJson(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatCorruptException($"JSON 解析失败: {ex.Message}", "$"); }

        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var fmt) || fmt.GetString() != "agedepth-export/1")
            throw new FormatCorruptException("缺少或未知的 format 标记，期望 'agedepth-export/1'", "$.format");
        if (!root.TryGetProperty("events", out var eventsEl) || eventsEl.ValueKind != JsonValueKind.Array)
            throw new FormatCorruptException("缺少 events 数组", "$.events");

        var importId = BeginImport();
        using var tx = _conn.BeginTransaction();
        try
        {
            var i = 0;
            foreach (var e in eventsEl.EnumerateArray())
            {
                var pos = $"$.events[{i}]";
                string Req(string name) => e.TryGetProperty(name, out var v)
                    ? v.GetString() ?? throw new FormatCorruptException($"字段 {name} 为空", $"{pos}.{name}")
                    : throw new FormatCorruptException($"缺少字段 {name}", $"{pos}.{name}");
                var seq = e.GetProperty("seq").GetInt64();
                var parents = e.GetProperty("parents").Deserialize<string[]>() ?? Array.Empty<string>();
                var payload = e.GetProperty("payload").GetRawText();
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO events(seq,id,kind,at,param_version,input_hash,parents,payload,import_id)
                    VALUES($seq,$id,$kind,$at,$pv,$ih,$parents,$payload,$importId)
                    """;
                cmd.Parameters.AddWithValue("$seq", seq);
                cmd.Parameters.AddWithValue("$id", Req("id"));
                cmd.Parameters.AddWithValue("$kind", Req("kind"));
                cmd.Parameters.AddWithValue("$at", Req("at"));
                cmd.Parameters.AddWithValue("$pv", Req("paramVersion"));
                cmd.Parameters.AddWithValue("$ih", Req("inputHash"));
                cmd.Parameters.AddWithValue("$parents", JsonSerializer.Serialize(parents));
                cmd.Parameters.AddWithValue("$payload", payload);
                cmd.Parameters.AddWithValue("$importId", importId);
                cmd.ExecuteNonQuery();
                i++;
            }
            tx.Commit();
            CommitImport(importId);
        }
        catch
        {
            tx.Rollback();
            Recover(); // 丢弃半写
            throw;
        }
    }

    public void Dispose()
    {
        _conn.Dispose();
        if (_ownedMemoryName is not null)
        {
            // 内存库随最后连接关闭而销毁
        }
    }
}
