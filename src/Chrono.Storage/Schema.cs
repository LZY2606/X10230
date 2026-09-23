namespace Chrono.Storage;

internal static class Schema
{
    public const string Sql = @"
CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS branches (
    id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(id),
    parent_branch_id TEXT,
    created_at_utc TEXT NOT NULL,
    frozen INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (project_id, id)
);

CREATE TABLE IF NOT EXISTS events (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    branch_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    parent_event_ids TEXT NOT NULL,
    kind TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    actor TEXT NOT NULL,
    rule_set_version TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    label TEXT,
    frozen INTEGER NOT NULL DEFAULT 0,
    UNIQUE (branch_id, sequence)
);

CREATE INDEX IF NOT EXISTS idx_events_project ON events(project_id);

CREATE TABLE IF NOT EXISTS material_snapshots (
    event_id TEXT PRIMARY KEY REFERENCES events(id),
    branch_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS derived_cache (
    event_id TEXT PRIMARY KEY REFERENCES events(id),
    branch_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    payload_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS imports (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    completed_at_utc TEXT,
    state TEXT NOT NULL,
    error TEXT
);
";
}
