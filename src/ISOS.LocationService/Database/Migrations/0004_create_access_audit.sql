-- Access audit (LLD 4.4): one row per read of another person's location data by a manager.
-- Who, whom, which data, which range, when. It never holds coordinates.
CREATE TABLE access_audit (
    audit_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    actor_party_id  text        NOT NULL,
    target_party_id text        NOT NULL,
    data_type       text        NOT NULL,
    range_requested text        NOT NULL
);

CREATE INDEX ix_access_audit_target ON access_audit (target_party_id, occurred_at);
CREATE INDEX ix_access_audit_actor ON access_audit (actor_party_id, occurred_at);
