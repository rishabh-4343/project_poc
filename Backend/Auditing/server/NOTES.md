# Auditing backend: working notes

Standalone `wfsa` (Windows File Server Auditing) backend. Tasks B0 to B5 of `context/auditor-pack/docs/TASKS-backend.md` are built and covered by the test scripts in `tests/`.

## Run it

```powershell
docker compose -f context/auditor-pack/deploy/docker-compose.yml up -d
dotnet run --project Backend/Auditing/server/server/server.csproj --urls http://127.0.0.1:5058
```

- Swagger UI: `http://127.0.0.1:5058/swagger`. OpenAPI document: `/openapi/v1.json`. Both are served in Development only.
- Typed client for the frontend: `npx openapi-typescript http://127.0.0.1:5058/openapi/v1.json -o src/api/schema.d.ts`.
- First start seeds the dev tenant, dev user and a `dev-agent`, and prints `DEV AGENT KEY (shown once)`. Only the hash is stored. To issue a new key: `docker exec auditor-pg psql -U postgres -d postgres -c "delete from core.agents where name='dev-agent'"` and restart.
- Sessions past `MaxEndAt` are stopped by `CommandExpiryWorker` (same path as a manual stop, `stopped_by` empty).
- Shares: on every agent connect, and when a share is registered while the agent is online, the backend sends the client method `UpdateShares({ serverId, shares[{ name, localPath }] })` (the full list for that server).
- `AgentOffline` integrity events are opened by the backend when an agent's last connection drops and closed when it reconnects. A backend crash leaves no event for that outage; the next connect closes any left open.
- `AgentUpdated` (the `GET /api/agents` row shape) is pushed to the UI tenant group on connect, disconnect and heartbeat.
- Integrity events are read at `GET /api/wfsa/integrity-events?limit=` (recent, tenant) and `GET /api/wfsa/sessions/{id}/integrity-events` (overlapping the session window).
- Tests: `pwsh Backend/Auditing/server/tests/run-all.ps1 -Yes`. They **delete** servers, sessions, commands and agents and truncate the `wfsa` ClickHouse tables in the local dev databases, so they refuse to run without `-Yes`.

## Configuration

| Key (env var form) | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Postgres` (`ConnectionStrings__Postgres`) | localhost:5433 | Config and app state. Host port 5433 because a native Windows PostgreSQL owns 5432 here. |
| `ConnectionStrings:ClickHouse` | localhost:8123 (HTTP) | Audit facts. |
| `Cors:AllowedOrigins` | `http://localhost:3000` | Browser origins for the API and hubs. |
| `Wfsa:CommandTtlSeconds` | 120 | How long an agent has to answer a command. |
| `Wfsa:ExpiryPollSeconds` | 5 | How often expired commands are failed. |

`appsettings.Development.json` overrides `appsettings.json`; change both when a connection string changes. The `devpassword` values are the docker-compose dev defaults only.

## Layout

```
server/Common/        Auth (DevAuth, X-Agent-Key), Db (core migrator, seeder, ConfigDbContext), Ch, Hubs, Modules, Servers
server/Modules/Wfsa/  the module: Data, Sessions, Commands, Ingestion, Reports
tests/                PowerShell test scripts (see above)
```

New modules implement `IServerModule` and are registered with one line: `builder.AddModule<WfsaModule>();`.

## Decisions that differ from the planning docs

- The PostgreSQL schema and the ClickHouse database are both named **`wfsa`**, not `audit`, so this module stays separate from the others. The renamed scripts are `schema/db/wfsa/*` and `schema/ch/wfsa/*`; the delivered `audit` scripts are untouched.
- `schema/db/wfsa/0002_ddc_contract.sql` creates the `ddc` schema, which belongs to the DDC developer. Move it out of the `wfsa` folder when merging.
- `core.servers` and `core.shares` endpoints live in `Common/Servers` (platform-owned data), at `/api/servers` and `/api/shares`. Sessions live in the module at `/api/wfsa/sessions`.
- Minimal APIs, not controllers (the guide says minimal APIs).
- No DbUp: two small migrators apply the SQL files (`CoreSchemaMigrator`, and `PostgresModuleMigrator` with per-schema tracking). ClickHouse scripts run only when the database has no tables.
- The module writes its own rows in `core.commands` (`module = 'wfsa'`), as B4 requires.
- Stopping a never-delivered session cancels its command and completes it at once. Running sessions go to `Stopping` and finish when the agent answers `StopScope`.
- Sessions can only be created on an approved server.

## Session lifecycle

`Requested` (command pending) to `Starting` (command sent) to `Active`, optionally `Degraded`, then `Stopping` to `Completed`. `Failed` on rejection or expiry. `Degraded` means the backend could not confirm the scope after an agent reconnect. A result that arrives after expiry or after a stop was requested is ignored.

## Realtime contract (SignalR, JSON, camelCase, enums as strings)

OpenAPI does not describe hubs, so the shapes are recorded here.

### Agent hub `/hubs/agent` (header `X-Agent-Key`; message limit 8 MB; at most 10,000 items per batch)

Agent calls:

- `EventBatch({ serverId, actions[] })` returns `{ serverId, lastSeq, stored, duplicates }`.
  Action: `tenantId, serverId, seq, occurredAt, objectType (File|Folder|Share), operation, success, certainty (Certain|Inferred), path, pathHash, newPath, shareName, actorSid, actorName, clientIp, processName, oldSecurity, newSecurity, details`. Operation names are the contract enum names (`Created`, `Modified`, ... `Other`).
- `ReadBatch({ batchId, serverId, reads[] })` returns the same ack.  `batchId` is required (Guid) and must be identical on a resend; a repeat is acked with `stored: 0, duplicates: n`. Read: `bucketStart, actorSid, pathHash, firstAt, lastAt, readCount, objectType, path, actorName, clientIp, processName, success`.
- `CommandResult({ commandId, success, error?, result? })`.
- `Heartbeat({ agentVersion, driverVersion, servers[{ serverId, droppedCount, ringLag, outboxSize, activeScopes }] })`: stores the versions and last-seen time, sets `wfsa.agent_positions.dropped_count` to the reported driver total per server, and pushes `AgentUpdated` to the UI tenant group. Shapes defined here; need the agent developer's sign-off.
- `IntegrityEvent({ serverId, kind, startedAt, endedAt?, firstSeq?, lastSeq?, droppedCount?, details? })`: `kind` is `DriverOverflow|Gap|GapRecovered|AgentOffline|DriverUnloaded|TamperSuspected`. Idempotent on `(serverId, kind, startedAt)`: a resend updates that row (use it to close an open event). A `DriverOverflow` also raises `dropped_count` to `droppedCount`.

Backend calls the agent (client methods `StartScope` and `StopScope`), each with `{ commandId, type, expiresAt, payload }`:

- `StartScope` payload: `{ sessionId, serverId, operations[], includeReads, folders[{ localPath, recursive, shareId? }], maxEndAt? }`.
- `StopScope` payload: `{ sessionId }`.

Agent rules:

- Ignore duplicate command ids.
- Treat a `StartScope` for a session already held as a no-op that answers success.
- On every connect the backend resends unanswered commands and a fresh `StartScope` for each `Active` or `Degraded` session.
- A resend of an action batch is safe: ingest is idempotent per `(tenantId, serverId, seq)`.

### UI hub `/hubs/ui` (open under DevAuth)

- Call `JoinSession(sessionId)` and `LeaveSession(sessionId)`. Joining another tenant's session fails.
- `ActionsAppended(sessionId, rows[])`: only newly stored rows inside that session's scope. Row fields match `ActionRow` in the OpenAPI document. Merge newest-first and dedupe by `(serverId, seq)`.
- `SessionUpdated({ id, serverId, state, failureReason, activeAt, stoppedAt, stoppedBy })`: sent to the tenant group, which every connection joins automatically. It is a slim update, so merge it by id.

## Open items and known gaps

Needs a decision:

1. **Read batch ids are never pruned.** `wfsa.read_batches` gets one small row per batch (idempotency key; the agent must resend the same `batchId`). Prune rows older than a few days when this matters. A crash between claiming the id and the ClickHouse write loses that batch.
2. **Contract shapes need the agent developer's sign-off:** the batch envelopes, `BatchAck`, and the command and result payloads above were defined here because the docs only name them.
3. **Agents API**: `GET /api/agents`, `POST /api/agents` (key shown once), `POST /api/agents/{id}/rotate-key` (new key, same agent and servers), `POST /api/agents/{id}/revoke`. A revoked or rotated key stops authenticating at once; a connection already open ends only when it next reconnects.

Deferred behaviour:

4. Content-view reads are stored but not in the report or CSV.
5. The report time window uses the agent's event time, so a skewed agent clock can move events in or out of a session.
6. Report queries use `FINAL` to hide unmerged resends; fine for a POC, slower on very large tables.
7. Ingest dedup is a check then insert, so two connections sending the same batch at the same instant could both store it. Fine with one agent per server.
8. If a command expires after the agent actually acted on it, the session reads `Failed` while the driver may still be watching.
9. `AgentHub` lives in `Common` but calls `wfsa` ingestion and command services. Another module will need per-module dispatch.
10. `core.tenants.retention_days` is unused; ClickHouse TTL is fixed at 365 days for actions and 90 for reads.
11. Auth is the DevAuth stub only: one seeded tenant and user, the UI hub is open, and agent keys are SHA-256 hashes with no rotation.
