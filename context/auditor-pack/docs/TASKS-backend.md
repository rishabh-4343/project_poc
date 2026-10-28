# TASKS — Backend / server (shared ASP.NET Core API)

## Context for a fresh agent
The backend is shared by three developers. Task **B0 is the common foundation** (one
owner, done first) and sets up the module pattern. Tasks **B1–B5 are the `wfsa` module**
(yours). Other devs copy B1–B5 shape for their own module slug. Read `docs/00-OVERVIEW.md`
and `docs/CONVENTIONS.md`. All work lives in `backend/server/`. You touch only
`Modules/Wfsa/`, `db/wfsa/`, `ch/wfsa/`, plus the three shared composition points (by PR).

Stores: PostgreSQL (config in schema `wfsa` + shared `core`) and ClickHouse (facts in db
`wfsa`). Auth is the DevAuth stub (one seeded tenant + dev user). Agent auth = `X-Agent-Key`.

---
### B0 · COMMON foundation  [assign ONE owner; do first]
- Goal: the host, both DB connections, DevAuth, hubs, and the module-registration pattern.
- Steps:
  1. `backend/server/Auditor.Server.sln`; projects `Auditor.Server` (web) and `Auditor.Contracts`.
  2. `deploy/docker-compose.yml`: postgres:17 + clickhouse:24.8 (already drafted in the schema set — reuse).
  3. `Common/Db`: Npgsql + EF Core `ConfigDb` (maps `core.*`); apply `db/core` migrations on startup (DbUp).
  4. `Common/Ch`: `ClickHouseConnectionFactory` + `ClickHouseBulkWriter`; apply `ch/*` migrations.
  5. `Common/Auth`: `ICurrentTenant`/`ICurrentUser` + `DevAuth` middleware injecting the seeded tenant/user;
     `AgentKeyAuthHandler` validating `X-Agent-Key` against `core.agents.api_key_hash`.
  6. `Common/Hubs`: `UiHub` (/hubs/ui, groups `tenant:{id}`,`session:{id}`) and `AgentHub` (/hubs/agent) base.
  7. `Program.cs`: a module-registration convention — each module exposes
     `IServerModule` with `MapEndpoints(app)` + `Register(services)`; host discovers/calls them.
     Composition point: `builder.AddModule<WfsaModule>();` (one line per module).
  8. Seed: on first run, insert one `core.tenants` row + one `core.users` admin + a dev `core.agents`
     row with a known API key (print it once).
- Done when: `docker compose up -d` + `dotnet run` boots; `/health` OK; migrations applied; swagger lists no module yet.

### B1 · Wfsa module skeleton
- Goal: the module folder wired into the host.
- Steps:
  1. `Modules/Wfsa/WfsaModule.cs` implementing `IServerModule` (slug `wfsa`, route base `/api/wfsa`).
  2. Register it: add `builder.AddModule<WfsaModule>();` to `Program.cs` (composition point).
  3. `db/wfsa/0001_*.sql` and `ch/wfsa/0001_*.sql` = the delivered audit schema, renamed to schema/db `wfsa`.
- Done when: swagger shows `/api/wfsa/ping` returning OK; `wfsa` schema + CH db exist after startup.

### B2 · Config endpoints (servers, shares, sessions CRUD)
- Goal: manage what gets audited.
- Steps:
  1. `Controllers/ServersController.cs` — list/register/approve servers (core.servers, filtered by tenant).
  2. `Controllers/SharesController.cs` — list/register shares.
  3. `Controllers/SessionsController.cs` — create (Requested), list, get, stop. Writes `wfsa.sessions` + `wfsa.session_folders`.
  4. All reads/writes via `ICurrentTenant`. Use EF Core for these (low volume).
- Done when: you can create a session via swagger and see it in PostgreSQL.

### B3 · Agent ingest (facts -> ClickHouse)
- Goal: receive batches from the agent and bulk-insert.
- Steps:
  1. `AgentHub` methods `EventBatch`/`ReadBatch` (auth = AgentKey) -> `Ingestion/IngestionService`.
  2. Verify the server belongs to the agent's tenant (cache).
  3. Stamp DDC fields from `ddc.file_classification` (left join by tenant+server+path_hash; nulls if absent).
  4. `ClickHouseBulkWriter` into `wfsa.actions` / `wfsa.read_actions`.
  5. Update `wfsa.agent_positions.last_seq = greatest(...)`; ack the batch.
- Done when: a simulated batch (a test client) lands in ClickHouse; a resent batch adds nothing after dedup.

### B4 · Sessions drive the agent
- Goal: start/stop a session issues scope commands to the agent and tracks state.
- Steps:
  1. On session create: write `core.commands`(type StartScope, module wfsa, folders+operations) and push to the agent's hub group.
  2. On the agent's CommandResult -> set session Active/Failed.
  3. On stop: StopScope command; on result -> Completed.
  4. `CommandExpiryWorker` fails sessions whose command expires.
- Done when: starting a session in the UI/swagger makes the agent begin capturing; stop ends it.

### B5 · Reports + live push
- Goal: serve report pages and push live rows.
- Steps:
  1. `Controllers/ReportController.cs`:
     - `GET /api/wfsa/sessions/{id}/actions` — query `wfsa.actions` by tenant+server+time+path prefix+operations, keyset on (occurred_at, seq), limit 200.
     - `GET /api/wfsa/sessions/{id}/export.csv` — streamed.
     - `GET /api/wfsa/dashboard` — read `wfsa.daily_action_counts`.
  2. After ingest, push new rows to `session:{id}` via `UiHub` (ActionsAppended).
- Done when: the report endpoint returns stored actions; the live view receives new rows.
