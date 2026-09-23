using Microsoft.Data.Sqlite;

namespace AgeDepthStudio.Persistence;

public static class Database
{
    public static string ConnectionStringFor(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 5
        }.ToString();
    }

    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS materials(
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                raw_json TEXT NOT NULL,
                norm_json TEXT,
                diag_json TEXT NOT NULL DEFAULT '[]',
                source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS params(
                id TEXT PRIMARY KEY,
                version INTEGER NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS segment_results(
                key TEXT PRIMARY KEY,
                section_id TEXT NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS candidates(
                id TEXT PRIMARY KEY,
                derived_seq INTEGER NOT NULL,
                json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS freezes(
                id TEXT PRIMARY KEY,
                candidate_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL,
                type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conclusions(
                seq INTEGER PRIMARY KEY,
                json TEXT NOT NULL);
            INSERT OR IGNORE INTO meta(key, value) VALUES ('freeze.version', '0');
            """;
        cmd.ExecuteNonQuery();
    }
}
