namespace ChronoCore.Storage;

internal static class Schema
{
    public const string Sql = """
CREATE TABLE IF NOT EXISTS projects(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS branches(
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  id TEXT NOT NULL,
  name TEXT NOT NULL,
  frozen_event_id TEXT,
  freeze_digest TEXT,
  created_at TEXT NOT NULL,
  PRIMARY KEY(project_id, id)
);
CREATE TABLE IF NOT EXISTS materials(
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  id TEXT NOT NULL,
  kind INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY(project_id, id)
);
CREATE TABLE IF NOT EXISTS paramsets(
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  id TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY(project_id, id)
);
CREATE TABLE IF NOT EXISTS decisions(
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  id TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY(project_id, id)
);
CREATE TABLE IF NOT EXISTS events(
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  branch_id TEXT NOT NULL,
  seq INTEGER NOT NULL,
  id TEXT NOT NULL UNIQUE,
  parent_event_id TEXT,
  type TEXT NOT NULL,
  parameter_set_id TEXT NOT NULL,
  input_digest TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  result_ref TEXT,
  actor TEXT NOT NULL,
  created_at TEXT NOT NULL,
  PRIMARY KEY(project_id, branch_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_events_ref ON events(result_ref);
CREATE TABLE IF NOT EXISTS results(
  id TEXT PRIMARY KEY,
  payload_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS import_ledger(
  project_id TEXT PRIMARY KEY,
  backup_path TEXT NOT NULL,
  checksum_before TEXT NOT NULL,
  checksum_after TEXT,
  status TEXT NOT NULL,
  started_at TEXT NOT NULL,
  finished_at TEXT
);
""";
}
