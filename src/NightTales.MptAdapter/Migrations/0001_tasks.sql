-- 11.3.5-T2 plan §3.1: the adapter's durable task store. One row per RenderTaskRecord (11 README D5).
-- No column holds the script, terms, prompts or any node path (05 D4 "never in a status", 11 D19 S7): the
-- submit body is hashed and discarded, and mpt_json keeps MPT's state/progress/failed_stage/warnings only.
--
-- MigrateAsync applies the PRAGMAs on the connection (journal_mode cannot change inside a transaction) and
-- runs everything after them in one transaction, then writes schema_meta['version'].

PRAGMA journal_mode = WAL;
PRAGMA synchronous  = FULL;
PRAGMA busy_timeout  = 5000;
PRAGMA foreign_keys  = ON;

CREATE TABLE IF NOT EXISTS tasks (
  task_id            TEXT    NOT NULL PRIMARY KEY,   -- UUIDv7 text (36 chars)
  render_job_id      TEXT    NOT NULL,
  body_sha256        TEXT    NOT NULL,               -- sha256 hex of the RAW submit body (D5)
  backend_task_id    TEXT        NULL,               -- MPT task id, internal only (05 D4)
  state              TEXT    NOT NULL,               -- queued|running|succeeded|failed|cancelled
  stage              TEXT    NOT NULL,               -- queued|voice|subtitles|images|assembling|checking|done
  percent            INTEGER NOT NULL DEFAULT 0 CHECK (percent BETWEEN 0 AND 100),
  cancel_requested   INTEGER NOT NULL DEFAULT 0 CHECK (cancel_requested IN (0,1)),
  submitted_at       TEXT    NOT NULL,               -- ISO-8601 UTC, 'Z'
  started_at         TEXT        NULL,
  backend_started_at TEXT        NULL,               -- MPT service start time at SubmitAsync (D6 lost rule)
  finished_at        TEXT        NULL,
  cleaned_at         TEXT        NULL,               -- 204 delete; row purged after 7 d (D5)
  outcome_json       TEXT        NULL,               -- RenderOutcomeV1
  output_json        TEXT        NULL,               -- RenderOutputV1
  check_json         TEXT        NULL,               -- render-check.v1 verbatim (05 D9)
  images_json        TEXT        NULL,               -- images.v1 from the relay (03 D6)
  mpt_json           TEXT        NULL,               -- last MPT snapshot for the status `mpt` block (05 D4)
  attempts           INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tasks_job_body ON tasks (render_job_id, body_sha256);
CREATE INDEX        IF NOT EXISTS ix_tasks_state    ON tasks (state);
CREATE INDEX        IF NOT EXISTS ix_tasks_cleaned  ON tasks (cleaned_at) WHERE cleaned_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS schema_meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
