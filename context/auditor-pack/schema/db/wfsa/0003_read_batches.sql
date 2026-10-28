-- Idempotency keys for ReadBatch: read_actions sums counts, so a resent batch must be recognised and skipped.
-- One small row per batch; rows older than a few days can be pruned.
CREATE TABLE wfsa.read_batches (
    batch_id     uuid PRIMARY KEY,
    tenant_id    uuid        NOT NULL,
    server_id    uuid        NOT NULL,
    received_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON wfsa.read_batches (received_at);
