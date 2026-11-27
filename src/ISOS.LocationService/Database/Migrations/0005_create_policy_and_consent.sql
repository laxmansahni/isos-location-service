-- Policy, consent, preferences and device sessions (LLD 2.1, ADR-004, ADR-005).
-- Everything is keyed by (party_id, client_id): the platform party id and the client the person belongs to.

-- Client configuration, normally set in the Admin Portal. No admin API exists yet, so rows are written by hand.
-- A client without a row gets the defaults from configuration (Policy:DefaultClient).
CREATE TABLE client_location_policy (
    client_id               text        PRIMARY KEY,
    sharing_enabled         boolean     NOT NULL,
    type_always             boolean     NOT NULL DEFAULT true,
    type_schedule           boolean     NOT NULL DEFAULT true,
    type_active_travel      boolean     NOT NULL DEFAULT true,
    type_event_window       boolean     NOT NULL DEFAULT true,
    allow_user_view_history boolean     NOT NULL DEFAULT true,
    retention_days          integer     NOT NULL DEFAULT 14 CHECK (retention_days > 0),
    event_window_minutes    integer     NOT NULL DEFAULT 60 CHECK (event_window_minutes > 0),
    visible_from            timestamptz,
    version                 integer     NOT NULL DEFAULT 1,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    updated_by              text
);

-- One row per person and client. visible_from is the visibility fence: points captured at or before it are
-- never shown or accepted again (ADR-005). It is set when consent is revoked.
CREATE TABLE user_location_consent (
    party_id           text        NOT NULL,
    client_id          text        NOT NULL,
    status             text        NOT NULL CHECK (status IN ('GRANTED', 'REVOKED')),
    disclosure_version text        NOT NULL,
    granted_at         timestamptz,
    revoked_at         timestamptz,
    visible_from       timestamptz,
    CONSTRAINT pk_user_location_consent PRIMARY KEY (party_id, client_id)
);

CREATE TABLE user_sharing_preference (
    party_id      text        NOT NULL,
    client_id     text        NOT NULL,
    primary_type  text        NOT NULL CHECK (primary_type IN ('ALWAYS', 'SCHEDULE', 'ACTIVE_TRAVEL')),
    home_timezone text,
    updated_at    timestamptz NOT NULL,
    CONSTRAINT pk_user_sharing_preference PRIMARY KEY (party_id, client_id)
);

-- Weekly windows in the traveller's local time. day_of_week: 0 = Sunday ... 6 = Saturday.
-- end_time before start_time means the window crosses midnight.
CREATE TABLE schedule_window (
    window_id   uuid     PRIMARY KEY DEFAULT gen_random_uuid(),
    party_id    text     NOT NULL,
    client_id   text     NOT NULL,
    day_of_week smallint NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
    start_time  time     NOT NULL,
    end_time    time     NOT NULL,
    CONSTRAINT ck_schedule_window_not_empty CHECK (start_time <> end_time)
);
CREATE INDEX ix_schedule_window_user ON schedule_window (party_id, client_id);

-- Additive windows opened by a trigger such as an AEC (ADR-004). Nothing writes them yet: the AEC feed is not built.
CREATE TABLE event_window_activation (
    activation_id uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    party_id      text        NOT NULL,
    client_id     text        NOT NULL,
    trigger_type  text        NOT NULL CHECK (trigger_type IN ('AEC', 'SPECIAL_ADVISORY')),
    source_ref    text,
    starts_at     timestamptz NOT NULL,
    expires_at    timestamptz NOT NULL,
    CONSTRAINT ck_event_window_order CHECK (expires_at > starts_at)
);
CREATE INDEX ix_event_window_user ON event_window_activation (party_id, client_id, expires_at);

CREATE TABLE device_session (
    party_id          text        NOT NULL,
    client_id         text        NOT NULL,
    device_id         text        NOT NULL,
    platform          text,
    state             text        NOT NULL CHECK (state IN ('ACTIVE', 'SUSPENDED_OS_PERMISSION', 'ENDED')),
    os_permission     text        NOT NULL,
    precise_location  boolean     NOT NULL,
    last_timezone     text,
    last_seen_at      timestamptz NOT NULL,
    lease_expires_at  timestamptz NOT NULL,
    CONSTRAINT pk_device_session PRIMARY KEY (party_id, client_id, device_id)
);
