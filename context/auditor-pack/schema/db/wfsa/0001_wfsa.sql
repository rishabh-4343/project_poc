-- =====================================================================
-- wfsa schema  (OWNED BY: auditing module). May read core; never writes core.
-- Config + bookkeeping only. The high-volume facts live in ClickHouse.
-- =====================================================================
CREATE SCHEMA IF NOT EXISTS wfsa;

CREATE TABLE wfsa.schema_migrations (
    version     text PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- The 24 report operations (object_type is separate: File/Folder/Share).
CREATE TYPE wfsa.operation AS ENUM (
    'Created','Modified','Deleted','Renamed','Moved','Copied',
    'Open','Read','ContentView','ReadBasicInfo','PropertyChanged',
    'PermissionChange','OwnerChange','AuditingChange',
    'ShareCreated','ShareRemoved','Other'
);
CREATE TYPE wfsa.object_type   AS ENUM ('File','Folder','Share');
CREATE TYPE wfsa.session_state AS ENUM ('Requested','Starting','Active','Degraded','Stopping','Completed','Failed');

-- A session = a scope the driver watches: server + folders + operations, started/stopped by a user.
CREATE TABLE wfsa.sessions (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    server_id       uuid        NOT NULL REFERENCES core.servers(id),
    agent_id        uuid        NOT NULL REFERENCES core.agents(id),
    name            text,
    operations      wfsa.operation[] NOT NULL,
    include_reads   boolean     NOT NULL DEFAULT false,
    state           wfsa.session_state NOT NULL DEFAULT 'Requested',
    started_by      uuid        NOT NULL REFERENCES core.users(id),
    stopped_by      uuid        REFERENCES core.users(id),
    requested_at    timestamptz NOT NULL DEFAULT now(),
    active_at       timestamptz,
    stopped_at      timestamptz,
    max_end_at      timestamptz,
    failure_reason  text,
    CHECK (cardinality(operations) > 0)
);
CREATE INDEX ON wfsa.sessions (tenant_id, state);
CREATE INDEX ON wfsa.sessions (server_id);

-- Folders in a session's scope (the driver is told to watch these paths).
CREATE TABLE wfsa.session_folders (
    session_id      uuid        NOT NULL REFERENCES wfsa.sessions(id) ON DELETE CASCADE,
    share_id        uuid        REFERENCES core.shares(id),
    local_path      text        NOT NULL,                  -- D:\Finance\Confidential
    recursive       boolean     NOT NULL DEFAULT true,
    PRIMARY KEY (session_id, local_path)
);

-- Dedup / resume high-water mark: the agent assigns a monotonic seq per server.
CREATE TABLE wfsa.agent_positions (
    tenant_id       uuid        NOT NULL,
    server_id       uuid PRIMARY KEY REFERENCES core.servers(id),
    agent_id        uuid        NOT NULL,
    last_seq        bigint      NOT NULL,                  -- highest action seq stored
    last_event_at   timestamptz,
    dropped_count   bigint      NOT NULL DEFAULT 0,        -- driver ring overflow total
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- Gaps, driver overflow, tamper, agent offline — shown on affected reports.
CREATE TABLE wfsa.integrity_events (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id       uuid        NOT NULL,
    server_id       uuid        NOT NULL REFERENCES core.servers(id),
    kind            text        NOT NULL CHECK (kind IN ('DriverOverflow','Gap','GapRecovered','AgentOffline','DriverUnloaded','TamperSuspected')),
    started_at      timestamptz NOT NULL,
    ended_at        timestamptz,
    first_seq       bigint,
    last_seq        bigint,
    details         jsonb,
    detected_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON wfsa.integrity_events (tenant_id, server_id, started_at DESC);

-- Online sources (Office 365 etc.), read by the backend, not the agent.
CREATE TABLE wfsa.online_connections (
    id                  uuid PRIMARY KEY,
    tenant_id           uuid    NOT NULL REFERENCES core.tenants(id),
    kind                text    NOT NULL,
    display_name        text    NOT NULL,
    external_tenant_id  text    NOT NULL,
    secret_ref          text,
    cursor              text,
    enabled             boolean NOT NULL DEFAULT true,
    last_polled_at      timestamptz,
    last_error          text,
    created_at          timestamptz NOT NULL DEFAULT now()
);
