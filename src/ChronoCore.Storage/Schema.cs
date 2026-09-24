namespace ChronoCore.Storage;

public static class Schema
{
    public const string Version = "3";

    public const string Ddl = """
    PRAGMA journal_mode=WAL;

    CREATE TABLE IF NOT EXISTS meta(
        key TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );

    -- Immutable material layer -------------------------------------------------
    CREATE TABLE IF NOT EXISTS packages(
        package_id      TEXT PRIMARY KEY,
        case_id         TEXT NOT NULL,
        case_version    TEXT NOT NULL,
        source_id       TEXT NOT NULL,
        raw_text        TEXT NOT NULL,
        input_digest    TEXT NOT NULL,
        imported_at     TEXT NOT NULL,
        ordinal         INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS package_objects(
        package_id      TEXT NOT NULL,
        object_kind     TEXT NOT NULL,  -- DATUM/SEGMENT/BOUNDARY/ANCHOR/EVENT/EXCLUSION/RAWLINE
        stable_id       TEXT NOT NULL,
        seq             INTEGER NOT NULL,
        raw_token       TEXT NOT NULL,
        normalized_json TEXT NOT NULL,
        source_line     INTEGER NOT NULL,
        PRIMARY KEY(package_id, object_kind, stable_id, seq)
    );

    -- Append-only event / decision layer --------------------------------------
    CREATE TABLE IF NOT EXISTS events(
        seq                 INTEGER PRIMARY KEY AUTOINCREMENT,
        event_id            TEXT NOT NULL UNIQUE,
        branch_id           TEXT NOT NULL,
        parent_event_id     TEXT,
        kind                TEXT NOT NULL,
        material_package_id TEXT,
        ruleset_version     TEXT NOT NULL,
        input_digest        TEXT NOT NULL,
        payload_json        TEXT NOT NULL,
        actor               TEXT NOT NULL,
        created_at          TEXT NOT NULL,
        frozen_candidate_id TEXT
    );
    CREATE INDEX IF NOT EXISTS ix_events_branch ON events(branch_id, seq);

    -- Derived results with provenance (never overwrite; each run is a row) ----
    CREATE TABLE IF NOT EXISTS derivations(
        derivation_id       TEXT PRIMARY KEY,
        event_id            TEXT NOT NULL,
        package_id          TEXT NOT NULL,
        input_digest        TEXT NOT NULL,
        ruleset_fingerprint TEXT NOT NULL,
        parent_dependency   TEXT NOT NULL,  -- prior derivation event id or 'ROOT'
        result_json         TEXT NOT NULL,
        created_at          TEXT NOT NULL
    );

    -- Per-candidate freeze lock with optimistic version token -----------------
    CREATE TABLE IF NOT EXISTS freezes(
        branch_id           TEXT PRIMARY KEY,
        candidate_id        TEXT NOT NULL,
        input_digest        TEXT NOT NULL,
        ruleset_fingerprint TEXT NOT NULL,
        lock_version        INTEGER NOT NULL,
        created_event_id    TEXT NOT NULL
    );

    -- Counterexample archive: baseline + single-source change evidence --------
    CREATE TABLE IF NOT EXISTS counterfactuals(
        archive_id      TEXT PRIMARY KEY,
        branch_id       TEXT NOT NULL,
        label           TEXT NOT NULL,
        baseline_pkg_id TEXT NOT NULL,
        changed_pkg_id  TEXT NOT NULL,
        changed_material TEXT NOT NULL,
        change_kind     TEXT NOT NULL,
        report_json     TEXT NOT NULL,
        adopted_json    TEXT NOT NULL,
        rejected_json   TEXT NOT NULL,
        created_at      TEXT NOT NULL
    );

    -- Export/import identity ledger -------------------------------------------
    CREATE TABLE IF NOT EXISTS identity_map(
        scope           TEXT NOT NULL,  -- 'native' or 'import:<bundle-id>'
        object_kind     TEXT NOT NULL,
        stable_id       TEXT NOT NULL,
        origin_id       TEXT NOT NULL,
        ordinal         INTEGER NOT NULL,
        PRIMARY KEY(scope, object_kind, origin_id, ordinal)
    );
    """;
}
