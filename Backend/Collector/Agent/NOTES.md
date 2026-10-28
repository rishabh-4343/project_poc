# Agent: working notes

`wfsa` agent (Worker Service, `net10.0-windows`, assembly `Auditor.Agent`). Tasks A1 to A7 of `context/auditor-pack/docs/TASKS-agent.md` are built, against a **simulated driver** (the kernel driver does not exist yet).

## Run it

```powershell
dotnet run --project Backend/Collector/Agent/Agent/Agent.csproj          # console
dotnet run --project Backend/Collector/Agent/Agent.Tests                 # unit checks (no test framework)
pwsh Backend/Collector/Agent/tests/e2e.ps1 -Yes                          # end to end against the backend + docker DBs
```

Configuration is the `Agent` section of `appsettings.json`; override with env vars (`Agent__ApiKey`, `Agent__ServerId`, ...).
The API key is never committed: register the server in the backend (`POST /api/servers`, approve it) and pass the dev agent key printed at the backend's first start.
`e2e.ps1` resets the local dev databases, like the backend tests, so it needs `-Yes`.

| Key | Default | Meaning |
|---|---|---|
| `BackendUrl` | http://localhost:5044 | Backend base URL (hub is `/hubs/agent`). |
| `ApiKey`, `ServerId`, `TenantId` | empty / empty / dev tenant | Identity; `X-Agent-Key` is the key. |
| `DataDirectory` | `%ProgramData%\Auditor\Agent` | `state/` (key + seq), `outbox/`, `logs/`. |
| `BatchMaxActions`, `BatchMaxMs` | 1000, 2000 | Batch cut. Max 10000 (backend limit). |
| `OutboxMaxMegabytes` | 2048 | Cap; over it, batches are dropped and a `Gap` event is sent. |
| `DriverSource` | `Simulated` | Only source implemented. |
| `SimulatedIntervalMs` | 500 | Rate of the simulated driver. |
| `ModifyMergeSeconds` | 5 | Window for merging repeated writes. |
| `HeartbeatSeconds` | 30 | |

## Pipeline

`DriverReader` (seq) -> `CorrelatorWorker` (Correlator, Enricher, Router) -> `BatcherWorker` (Batcher, DiskOutbox) -> `UploadWorker` -> backend.
`AgentChannel` holds the SignalR connection and applies `StartScope`/`StopScope` through `CommandHandler` and `ScopeManager`. `HeartbeatWorker` reports health.
Hosted services are registered so shutdown flows driver, correlator, batcher: a clean stop loses nothing.

## Decisions and deviations

- Folder is `Backend/Collector/Agent` (not `backend/agent`); the namespace is `Auditor.Agent`. The contract file `Driver/AuditRecord.cs` is a byte-identical copy of the frozen one. The simulated source has its own encoder for it.
- **seq** is reserved in blocks of 1000 in the encrypted store, so after a crash seq continues above everything used (a gap, never a reused number). A clean stop gives the block back.
- **Encryption**: random AES-256-GCM key in `state/key.bin`, wrapped with DPAPI machine scope. The outbox and the seq file are sealed with it.
- **Reads** (`Read`, `ContentView`) never go in action batches. They merge per hour, user and path into `ReadBatch`es, each with a fixed `batchId` so a resend is skipped by the backend.
- `Path` in an action stays the local path (the backend matches scopes on it); only `ShareName` is added. No UNC path is sent because the backend contract has no field for it.
- Package versions: all 10.0.0 except `Microsoft.AspNetCore.SignalR.Client` 10.0.14 (10.0.0 has a known high-severity advisory).

## Not done / open

1. **No real driver source.** Needs the WDK driver and the D2 scope message; `IDriverSource` and `IDriverControl` are the seams. Nothing here was run against a real driver.
2. **Copy inference** (optional in A3) is not built. The Correlator also does not recognise a delete-on-close from the driver (it only merges repeated `Deleted`).
3. **Events held in the Correlator (up to `ModifyMergeSeconds`) are lost on a hard crash**; the driver ring is memory-only, so they cannot be replayed. `SafeSeq` is exposed but nothing checkpoints on it yet.
4. **`UpdateScope`, `Ping`** are not handled: the backend does not send them. `UpdateShares` is handled and fills `shareName`.
5. Integrity events are in memory until sent; a crash before the next connect loses them.
6. Not verified: installing as a Windows service (`sc create`), running as a service account, and the lookup of real domain accounts (only well-known SIDs were checked).
7. A refused batch is set aside as `*.rejected` after 5 refusals; there is no alert or retry path for those yet.
