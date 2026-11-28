-- Purge job (LLD 5.3).

-- The job deletes rows past their retention date from every daily partition. Without this index that is a full
-- scan of about two weeks of history per run. A partitioned table cannot be indexed CONCURRENTLY, so on a large
-- production table this statement blocks writes while it runs; apply it in a maintenance window.
CREATE INDEX ix_location_history_retention ON location_history (retention_expires_at);

-- One row per purge run: the compliance evidence of what was removed and when (LLD 5.3 step 4).
-- It holds counts and partition names only: no party ids, no coordinates.
CREATE TABLE purge_run (
    run_id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    started_at           timestamptz NOT NULL,
    finished_at          timestamptz,
    status               text        NOT NULL CHECK (status IN ('RUNNING', 'SUCCEEDED', 'FAILED')),
    -- The UTC date the run treated as today: rows whose retention_expires_at is before it were expired.
    retention_cutoff     date        NOT NULL,
    expired_history_rows bigint      NOT NULL DEFAULT 0,
    fenced_history_rows  bigint      NOT NULL DEFAULT 0,
    expired_latest_rows  bigint      NOT NULL DEFAULT 0,
    fenced_latest_rows   bigint      NOT NULL DEFAULT 0,
    dropped_partitions   text[]      NOT NULL DEFAULT '{}',
    error                text
);

CREATE INDEX ix_purge_run_started ON purge_run (started_at);
