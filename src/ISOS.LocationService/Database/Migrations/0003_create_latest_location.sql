-- Latest known position per person (LLD 2.3). One row per party; a newer capture time replaces it.
-- It holds points captured with live sharing on (shared = true) and points captured only for AEC or
-- Location Based Alerts (shared = false, "not shared"). Only shared rows may ever be shown to managers.
-- Rebuildable from location_history for shared points only; not-shared points exist nowhere else.
CREATE TABLE latest_location (
    party_id             text             NOT NULL,
    client_id            text,
    device_id            text             NOT NULL,
    point_id             uuid             NOT NULL,
    captured_at          timestamptz      NOT NULL,
    utc_offset_min       smallint,
    received_at          timestamptz      NOT NULL,
    latitude             double precision NOT NULL,
    longitude            double precision NOT NULL,
    accuracy_m           real             NOT NULL,
    speed_mps            real,
    course_deg           real,
    altitude_m           real,
    quality_flags        integer          NOT NULL DEFAULT 0,
    shared               boolean          NOT NULL,
    retention_expires_at date             NOT NULL,
    CONSTRAINT pk_latest_location PRIMARY KEY (party_id)
);

-- The purge job deletes rows past their retention date.
CREATE INDEX ix_latest_location_retention ON latest_location (retention_expires_at);

-- Rows already in history were all shared points; keep the newest per person.
INSERT INTO latest_location (
    party_id, client_id, device_id, point_id, captured_at, utc_offset_min, received_at,
    latitude, longitude, accuracy_m, speed_mps, course_deg, altitude_m, quality_flags, shared, retention_expires_at)
SELECT DISTINCT ON (party_id)
    party_id, client_id, device_id, point_id, captured_at, utc_offset_min, received_at,
    latitude, longitude, accuracy_m, speed_mps, course_deg, altitude_m, quality_flags, shared, retention_expires_at
FROM location_history
ORDER BY party_id, captured_at DESC;
