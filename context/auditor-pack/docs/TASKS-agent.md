# TASKS — Agent (C# Windows service)   [owner: you]

## Context for a fresh agent
You are building the user-mode **agent**: it reads records from the kernel driver,
normalizes them into audit actions, buffers them, and uploads to the backend. This is the
`wfsa` module's processing layer. Read `docs/00-OVERVIEW.md` and `docs/CONVENTIONS.md`.
All work lives in `backend/agent/`. The backend contracts you send are in
`backend/server/src/Auditor.Contracts` (shared). Action identity = (tenant_id, server_id,
seq); you assign `seq`. Do tasks in order.

---
### A1 · Agent scaffold (console + service)
- Goal: a .NET 10 Worker Service that runs as console or Windows service, with config + logging.
- Steps:
  1. `backend/agent/Auditor.Agent.sln`, project `src/Auditor.Agent` (`net10.0-windows`).
  2. `Host.CreateApplicationBuilder` + `AddWindowsService`. Bind `AgentOptions` from `appsettings.json`
     (`BackendUrl`, `ApiKey`, `ServerId`, `TenantId`, `DataDirectory`, batch/outbox settings).
  3. Console + rolling-file logging.
- Done when: `dotnet run` starts and logs "agent started"; `sc create` installs it as a service.
- If resuming: if `src/Auditor.Agent` builds and runs, A1 is done.

### A2 · DriverReader + seq + persistence
- Goal: connect to the driver, receive records, assign monotonic `seq` per server, persist it.
- Steps:
  1. Connect to the driver port (`FilterConnectCommunicationPort`), send the current scope (empty for now).
  2. Receive `AUDIT_RECORD` bytes (shared `audit_record.h` layout); parse into `RawEvent`.
  3. Assign `seq = ++lastSeq` where `lastSeq` is loaded from the local store at startup and
     persisted (see A6 store). Emit nothing upstream yet — just log.
  4. On an overflow signal, create an `IntegrityEvent(DriverOverflow)`.
- Creates: `Driver/DriverReader.cs`, `Driver/RawEvent.cs`, `Driver/AuditRecord.cs` (mirror of the header).
- Done when: records from driver D5 are printed with increasing seq; seq continues across restarts.

### A3 · Correlator (typed actions)
- Goal: turn RawEvents into normalized actions.
- Steps:
  1. Map operations; rename/move already carry old+new from the driver (certainty Certain).
  2. Merge repeated Modify on the same (user,path) within N seconds.
  3. Delete: disposition+cleanup -> one Deleted.
  4. Copy (optional, Inferred): read-of-source then create-of-dest, same process, short window, equal name.
  5. Track `SafeSeq` (lowest seq still held in open correlation) so the Batcher never checkpoints past it.
- Creates: `Pipeline/Correlator.cs`.
- Done when: a scripted create/modify/delete/rename yields exactly the expected actions.

### A4 · Enricher
- Goal: add account name, UNC path, process name, path_hash.
- Steps:
  1. SID -> account via cached `LookupAccountSid` (1h TTL).
  2. local path -> UNC using the server's share list (pushed by backend via UpdateShares; stub for now).
  3. process name from the record (driver supplies PID; resolve+cache).
  4. `path_hash` = 64-bit hash of `lower(path)` — **use the exact function the DDC module uses** (agree the algorithm now; e.g. xxHash64 of UTF-8 lowercased path).
- Creates: `Pipeline/Enricher.cs`.
- Done when: actions carry actorName, uncPath, processName, pathHash.

### A5 · ScopeManager + Router
- Goal: apply the active session scope; push it to the driver.
- Steps:
  1. Receive StartScope/StopScope/UpdateScope from the backend (A7); maintain the union of active scopes.
  2. Push the union (paths + operation mask) to the driver (D2 message).
  3. Router keeps only actions inside an active scope before batching.
- Creates: `Pipeline/ScopeManager.cs`, `Pipeline/Router.cs`.
- Done when: starting a scope makes matching actions flow; stopping it stops them.

### A6 · Batcher + encrypted Outbox + resume
- Goal: durable buffering; nothing lost on crash or backend outage.
- Steps:
  1. Batch at `BatchMaxActions` or `BatchMaxMs`.
  2. Write each batch to `DataDirectory/outbox` as an encrypted file (AES-GCM; key via DPAPI machine scope); atomic temp+rename.
  3. `ILocalStore` (small encrypted JSON) persists `lastSeq` per server.
  4. On startup, reload lastSeq and resume; re-send unacked outbox files.
- Creates: `Outbox/Outbox.cs`, `Storage/LocalStore.cs`.
- Done when: kill the agent mid-run -> on restart it resumes with no gap and no duplicate after dedup.

### A7 · Control channel + Uploader + heartbeat
- Goal: outbound connection to the backend; upload batches; receive commands.
- Steps:
  1. SignalR client to `BackendUrl/hubs/agent`, auth header `X-Agent-Key: <ApiKey>`; auto-reconnect.
  2. Uploader drains the outbox oldest-first via `EventBatch`/`ReadBatch`; delete a file only after ack.
  3. Handle StartScope/StopScope/UpdateScope/UpdateShares/Ping; ignore duplicate command ids.
  4. Heartbeat every 30s (version, lag, droppedCount, outbox size, active scopes); send IntegrityEvents.
- Creates: `Channel/AgentChannel.cs`, `Workers/UploadWorker.cs`, `Workers/HeartbeatWorker.cs`.
- Done when: actions appear in the backend/ClickHouse; stop the backend -> outbox grows -> restart -> all arrive, none duplicated.
