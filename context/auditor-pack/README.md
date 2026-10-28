# Auditor — Preparation Pack (driver approach)

Everything needed to start building the Windows File Server Auditing module and its
shared backend/frontend, with PostgreSQL + ClickHouse. No code yet — specs, schema,
plan, and the frozen driver/agent contract.

## Read in this order
1. docs/INDEX.md            — the map
2. docs/00-OVERVIEW.md      — what we build, LOCKED decisions, build order, auth decision
3. docs/CONVENTIONS.md      — exact folder tree, naming, module isolation, migrations, git
4. docs/CONTRACTS.md  (in contract/) — frozen driver<->agent record format + path_hash
5. docs/TASKS-*.md          — self-contained task lists per stream
6. docs/guides/01..05       — deeper how-to per component

## Contents
- docs/            the plan + development guides
- contract/        CONTRACTS.md + audit_record.h (C++) + AuditRecord.cs (C#) — byte-exact, verified 32-byte header
- schema/          PostgreSQL (db/) + ClickHouse (ch/) schema, one folder per module
- deploy/          docker-compose.yml (postgres:17 + clickhouse:24.8)

## Status notes
- Approach: KERNEL DRIVER (C++) + agent (C#) + backend (C#) + frontend (Next.js).
- Stores: PostgreSQL (config) + ClickHouse (facts). No TimescaleDB.
- Auth: skipped for now behind a DevAuth stub (seam kept). Agent auth = X-Agent-Key.
- DDC: out of scope for now. The schema keeps nullable DDC columns and a db/audit/0002
  contract table for later; ignore them until DDC is built.
- Module slug for this module = `wfsa`. The schema files currently use the schema/db name
  `audit` as a placeholder — rename to `wfsa` per docs/TASKS-backend.md task B1 so the
  three auditing modules don't collide on the name.

## Quick start (once you begin coding)
1. cd deploy && docker compose up -d        # postgres + clickhouse
2. Apply schema: db/core first, then db/audit (PG); ch/audit (ClickHouse).
3. Backend task B0 and frontend task F0 (the common foundations) first; then driver D1 + agent A1.

Validated here: PostgreSQL schema applies cleanly; ClickHouse schema applies and the
dashboard materialized view auto-fills; the C header is exactly 32 bytes and the C#
reader parses a matching record. ClickHouse TTL/retention and the driver itself must be
tested on a real server.
