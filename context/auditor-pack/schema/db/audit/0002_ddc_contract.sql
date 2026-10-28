-- =====================================================================
-- OWNED BY: DDC module.  Shown here only because audit reads it at ingest.
-- In the repo this lives under db/ddc/, maintained by the DDC developer.
-- audit stamps these fields onto each action; it never writes this table.
-- =====================================================================
CREATE SCHEMA IF NOT EXISTS ddc;
CREATE TABLE IF NOT EXISTS ddc.file_classification (
    tenant_id       uuid        NOT NULL,
    server_id       uuid        NOT NULL,
    path_hash       bigint      NOT NULL,
    path            text        NOT NULL,
    content_type    text,
    risk_level      smallint,
    compliance_tags text[],
    monetary_value  numeric(14,2),
    classified_at   timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, server_id, path_hash)
);
