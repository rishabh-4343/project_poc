-- =====================================================================
-- ClickHouse database: audit   (OWNED BY: auditing module)
-- The high-volume facts. One ClickHouse DB per module (audit, ddc, ...).
-- Identity / dedup: (tenant_id, server_id, seq) — seq assigned by the agent per server.
-- =====================================================================
CREATE DATABASE IF NOT EXISTS audit;

-- Changes and failed access (everything except content views).
CREATE TABLE audit.actions
(
    tenant_id      UUID,
    server_id      UUID,
    seq            UInt64,                         -- agent's monotonic sequence per server (dedup + ordering)
    occurred_at    DateTime64(3, 'UTC'),
    object_type    LowCardinality(String),         -- File, Folder, Share
    operation      LowCardinality(String),         -- Created, Modified, Deleted, Renamed, ...
    success        Bool,                            -- Allowed / Not allowed
    certainty      LowCardinality(String),         -- Certain, Inferred
    path           String CODEC(ZSTD(3)),
    path_hash      UInt64,                          -- hash of lower(path): one file's history
    new_path       String CODEC(ZSTD(3)),          -- rename/move destination (driver gives it exactly)
    share_name     LowCardinality(String),
    actor_sid      LowCardinality(String),
    actor_name     LowCardinality(String),         -- Who
    client_ip      String,                          -- From
    process_name   LowCardinality(String),          -- Process name (driver has it)
    old_security   String CODEC(ZSTD(3)),           -- SDDL before (permission/owner/auditing changes)
    new_security   String CODEC(ZSTD(3)),           -- SDDL after
    -- DDC snapshot, stamped at ingest (classification at the time of the action)
    content_type   LowCardinality(String),
    risk_level     Int8 DEFAULT -1,                 -- -1 = unknown
    compliance_tags Array(LowCardinality(String)),
    monetary_value Decimal(14,2) DEFAULT 0,
    details        String CODEC(ZSTD(3))
)
ENGINE = ReplacingMergeTree                          -- a resent batch (same key) collapses on merge
PARTITION BY toYYYYMM(occurred_at)
ORDER BY (tenant_id, server_id, occurred_at, seq)
TTL toDateTime(occurred_at) + INTERVAL 365 DAY;      -- <retention_days> per tenant via ALTER

-- Content views ("File/Folder Content View"), merged by the agent: one row per user+file+hour.
CREATE TABLE audit.read_actions
(
    tenant_id      UUID,
    server_id      UUID,
    bucket_start   DateTime('UTC'),                 -- the hour
    actor_sid      LowCardinality(String),
    path_hash      UInt64,
    first_at       DateTime64(3, 'UTC'),
    last_at        DateTime64(3, 'UTC'),
    read_count     UInt32,
    object_type    LowCardinality(String),
    path           String CODEC(ZSTD(3)),
    actor_name     LowCardinality(String),
    client_ip      String,
    process_name   LowCardinality(String),
    success        Bool DEFAULT true,
    content_type   LowCardinality(String),
    risk_level     Int8 DEFAULT -1,
    compliance_tags Array(LowCardinality(String)),
    monetary_value Decimal(14,2) DEFAULT 0
)
ENGINE = SummingMergeTree(read_count)                -- same key: counts sum on merge
PARTITION BY toYYYYMM(bucket_start)
ORDER BY (tenant_id, server_id, bucket_start, actor_sid, path_hash)
TTL bucket_start + INTERVAL 90 DAY;                  -- reads kept shorter

-- Dashboard rollup: a small target table + a materialized view that fills it on every insert.
CREATE TABLE audit.daily_action_counts
(
    tenant_id   UUID,
    day         Date,
    server_id   UUID,
    operation   LowCardinality(String),
    object_type LowCardinality(String),
    success     Bool,
    actor_sid   LowCardinality(String),
    actions     UInt64
)
ENGINE = SummingMergeTree(actions)
PARTITION BY toYYYYMM(day)
ORDER BY (tenant_id, day, server_id, operation, object_type, success, actor_sid);

CREATE MATERIALIZED VIEW audit.daily_action_counts_mv TO audit.daily_action_counts AS
SELECT tenant_id, toDate(occurred_at) AS day, server_id, operation, object_type, success, actor_sid,
       count() AS actions
FROM audit.actions
GROUP BY tenant_id, day, server_id, operation, object_type, success, actor_sid;
