-- =====================================================================
-- core schema  (SHARED — owned by the platform owner, changes reviewed)
-- Every module may READ core and reference its ids. No module writes core
-- or alters it. tenant_id is on every table: one database, many clients.
-- =====================================================================
CREATE SCHEMA IF NOT EXISTS core;

-- Per-module migration tracking lives in each module's own schema (see audit.schema_migrations).
CREATE TABLE core.schema_migrations (
    version     text PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE core.tenants (
    id              uuid PRIMARY KEY,
    name            text        NOT NULL,
    slug            text        NOT NULL UNIQUE,
    status          text        NOT NULL DEFAULT 'active' CHECK (status IN ('active','suspended','deleting')),
    retention_days  int         NOT NULL DEFAULT 365,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE core.users (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    email           text        NOT NULL,
    display_name    text        NOT NULL,
    role            text        NOT NULL CHECK (role IN ('admin','auditor','viewer')),
    password_hash   text,                                   -- MVP; null under OIDC
    external_id     text,
    disabled_at     timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, email)
);

-- An agent = the C# service on one file server, paired with its kernel driver.
CREATE TABLE core.agents (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    name            text        NOT NULL,
    api_key_hash    bytea       UNIQUE,                     -- MVP
    cert_thumbprint text        UNIQUE,                     -- production mutual-TLS
    driver_version  text,
    agent_version   text,
    status          text        NOT NULL DEFAULT 'offline' CHECK (status IN ('online','offline','revoked')),
    last_seen_at    timestamptz,
    revoked_at      timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    CHECK (api_key_hash IS NOT NULL OR cert_thumbprint IS NOT NULL)
);
CREATE INDEX ON core.agents (tenant_id);

CREATE TABLE core.servers (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    agent_id        uuid        NOT NULL REFERENCES core.agents(id),
    host_name       text        NOT NULL,
    os_version      text,
    approved        boolean     NOT NULL DEFAULT false,
    removed_at      timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, host_name)
);
CREATE INDEX ON core.servers (agent_id) WHERE removed_at IS NULL;

CREATE TABLE core.shares (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    server_id       uuid        NOT NULL REFERENCES core.servers(id),
    name            text        NOT NULL,
    local_path      text        NOT NULL,                  -- D:\Finance
    unc_path        text        NOT NULL,                  -- \\fs01\Finance
    approved        boolean     NOT NULL DEFAULT false,
    removed_at      timestamptz,
    UNIQUE (server_id, local_path)
);

-- Commands to agents (all modules). ref_id points into the owning module's table.
CREATE TABLE core.commands (
    id              uuid PRIMARY KEY,
    tenant_id       uuid        NOT NULL REFERENCES core.tenants(id),
    agent_id        uuid        NOT NULL REFERENCES core.agents(id),
    module          text        NOT NULL,                  -- 'audit', 'ddc', ...
    type            text        NOT NULL,                  -- 'StartScope','StopScope','UpdateScope','Ping'
    payload         jsonb       NOT NULL,
    ref_id          uuid,
    status          text        NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Sent','Succeeded','Failed','Expired')),
    issued_at       timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    completed_at    timestamptz,
    result          jsonb,
    error           text,
    CHECK (expires_at > issued_at)
);
CREATE INDEX ON core.commands (agent_id, issued_at) WHERE status IN ('Pending','Sent');
CREATE INDEX ON core.commands (ref_id) WHERE ref_id IS NOT NULL;

-- Product's own audit trail (who did what in the app), hash-chained.
CREATE TABLE core.product_audit_log (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id   uuid        NOT NULL,
    at          timestamptz NOT NULL DEFAULT now(),
    user_id     uuid,
    action      text        NOT NULL,
    target      text,
    details     jsonb,
    prev_hash   bytea,
    row_hash    bytea       NOT NULL
);
CREATE INDEX ON core.product_audit_log (tenant_id, at DESC);
