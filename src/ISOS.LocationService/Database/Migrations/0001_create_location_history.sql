-- Location history (LLD 2.2). Daily range partitions on captured_at so purge is a partition drop.
-- A unique index on a partitioned table must include the partition column, so the idempotency
-- key is (party_id, captured_at, point_id) (ADR-010).
CREATE TABLE location_history (
    point_id             uuid             NOT NULL,
    party_id             text             NOT NULL,
    client_id            text,
    device_id            text             NOT NULL,
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
    retention_expires_at date             NOT NULL,
    CONSTRAINT pk_location_history PRIMARY KEY (party_id, captured_at, point_id)
) PARTITION BY RANGE (captured_at);

-- Creates one partition per UTC day for the given range if it does not exist yet.
-- Returns the number of partitions created. Safe to call repeatedly and concurrently.
CREATE FUNCTION ensure_location_history_partitions(first_day date, last_day date)
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    day_start date;
    partition_name text;
    created integer := 0;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('location_history_partitions'));

    FOR day_start IN SELECT generate_series(first_day, last_day, interval '1 day')::date LOOP
        partition_name := format('location_history_p%s', to_char(day_start, 'YYYYMMDD'));
        IF to_regclass(partition_name) IS NULL THEN
            EXECUTE format(
                'CREATE TABLE %I PARTITION OF location_history FOR VALUES FROM (%L) TO (%L)',
                partition_name,
                (day_start::timestamp AT TIME ZONE 'UTC'),
                ((day_start + 1)::timestamp AT TIME ZONE 'UTC'));
            created := created + 1;
        END IF;
    END LOOP;

    RETURN created;
END;
$$;
