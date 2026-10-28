# Auditor — Architecture (driver-based file server auditing)

## Components

| Component | Language | Runs on | Job |
|---|---|---|---|
| Minifilter driver | C/C++ (WDK) | Each file server (kernel) | Capture every file operation, hand records to the agent |
| Agent | C# (.NET, Windows service) | Each file server (user mode) | Filter, correlate, enrich, buffer, upload |
| Backend API | C# (ASP.NET Core) | Cloud or on-prem | Auth, sessions, ingest, reports |
| Frontend | React / Next.js | Browser | Sessions, live view, reports |
| PostgreSQL | — | With backend | Config + app state (one schema per module) |
| ClickHouse | — | With backend | Audit facts (one database per module) |

## Data flow

```
file operation
   -> kernel: minifilter driver captures (op, user SID, path, old/new name, status)
   -> shared ring buffer / FltSendMessage
   -> agent: Reader -> Correlator -> Enricher -> Router(scope) -> Batcher -> Outbox
   -> outbound mTLS (gRPC/SignalR) to backend
   -> backend: validate -> stamp DDC -> bulk INSERT into ClickHouse audit.actions
                                      -> update PostgreSQL positions/counts
   -> live hub pushes new actions to the browser
```

Control path is the reverse: backend issues StartScope/StopScope/UpdateScope to the
agent over the same connection; the agent programs the driver's watch scope.

## Why a driver (vs agentless)

The driver sees the real operation in the kernel, so: exact rename/move (old+new path
in one event), real process name, clean create/delete, and no dependence on Windows
audit policy or SACLs. Sessions become real start/stop (the driver's scope), not log
filters.

## Store split

- **PostgreSQL**: small, transactional, fast point lookups — config and sessions.
- **ClickHouse**: billions of append-only rows, columnar, heavy compression, fast
  summaries — the audit facts. Measured 2 crore rows: 0.55 GB vs 8.7 GB in Postgres;
  ~850k rows/s ingest; dashboard scans ~4–5x faster. Gap widens with scale.

## Action identity

No Security-log record number (the driver is the source). An action is
**(tenant_id, server_id, seq)**; `seq` is a monotonic counter the agent assigns per
server. A resent batch collapses via ClickHouse ReplacingMergeTree.

## Reliability invariants

- Driver ring overflow -> dropped-count + gap; never block file I/O.
- Agent outbox on disk; a batch is deleted only after the backend confirms storage.
- Agent resumes from its last persisted seq after restart.
- Integrity events (overflow, gap, agent offline, driver unloaded) surface on reports.
