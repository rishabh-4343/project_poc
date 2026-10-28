# Auditor — Project Overview (read this first)

This is the single source of truth. If you are an AI agent or a developer picking this
up cold, read this file, then `CONVENTIONS.md`, then your stream's `TASKS-*.md`.

## What we are building
A data-security auditing product. First module: **Windows File Server Auditing**,
captured via a **kernel minifilter driver** (no agentless/event-log approach). A C++
driver captures file operations; a C# agent normalizes and uploads them; a shared C#
backend ingests them; a shared Next.js frontend shows sessions and reports.

Two more modules (online-component auditing) are built by two other developers against
the **same** backend and frontend, in their own isolated folders.

## The three work streams
| Stream | Owner | Folders (exclusive) |
|---|---|---|
| Driver + Agent | **You** | `backend/driver/`, `backend/agent/` |
| Backend API (server) | **Shared** (3 devs, module-isolated) | `backend/server/` — each dev owns one module folder |
| Frontend (client) | **Shared** (3 devs, module-isolated) | `frontend/client/` — each dev owns one feature folder |

## Locked decisions (do not re-litigate; change only by updating this file)
1. **Driver = C/C++ (WDK).** Agent, backend, frontend = C#/TypeScript.
2. **Stores:** PostgreSQL (config + app state) + ClickHouse (audit facts). **No TimescaleDB.** Both via Docker.
3. **Multi-tenant, single database.** `tenant_id` on every table. One **PostgreSQL schema per module**, one **ClickHouse database per module**.
4. **Module slug** identifies a module everywhere (schema, CH db, folders, DI). This module's slug = **`wfsa`** (Windows File Server Auditing).
5. **Action identity = (tenant_id, server_id, seq).** `seq` = monotonic counter the agent assigns per server (no Windows record number with a driver). ClickHouse ReplacingMergeTree collapses resends.
6. **Auth: skipped for now, seam kept.** A `DevAuth` stub injects one seeded tenant + dev user behind `ICurrentUser`/`ICurrentTenant`. Agent↔backend uses a shared `X-Agent-Key` header. Replace with JWT/OIDC + mTLS later with no call-site changes.
7. **Sessions = driver scopes.** Start/stop a session = tell the driver which folders+operations to watch. Reads ("Content View") are part of the scope (the driver sees reads; no SACL).
8. **DDC fields are stamped onto each action at ingest** (copied from the DDC module's table), so reports never join across modules or stores.

## Reliability invariants (every stream respects these)
- Driver ring overflow -> dropped-count + integrity event; never block file I/O.
- Agent writes an encrypted on-disk outbox; a batch is deleted only after the backend acks durable storage.
- Agent resumes from its last persisted `seq` after a restart.
- Reports show integrity events (overflow, gap, agent offline) for the affected period.

## Build order (cross-stream)
1. **COMMON backend foundation** (`backend/server`, task B0) — one owner, first. Unblocks everyone.
2. **COMMON frontend foundation** (`frontend/client`, task F0) — one owner, first.
3. Driver↔agent loop (D1–D5, A1–A2) in parallel.
4. Agent pipeline (A3–A7) + backend wfsa module (B1–B5).
5. Frontend wfsa feature (F1–F4).

## Definition of "done" for the POC
A session started from the UI on a real folder shows Created/Modified/Deleted/Renamed
(and one denied) live, end to end: driver -> agent -> backend -> ClickHouse -> browser,
with the agent surviving a restart and buffering through a backend outage.
