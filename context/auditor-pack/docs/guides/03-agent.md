# Auditor — Agent (C# Windows service)

One per file server. Reads the driver's records, turns them into normalized actions,
buffers, and uploads. Plain user mode: no kernel constraints, full .NET.

## Project
- .NET 10 Worker Service, `net10.0-windows`, published self-contained single-file.
- `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.AspNetCore.SignalR.Client`
  (or Grpc.Net.Client), `System.Threading.Channels`.

## Pipeline (bounded channels between stages -> back-pressure)

```
DriverReader -> Correlator -> Enricher -> Router -> Batcher -> Outbox -> Uploader
                                                       ^control: ScopeManager (driver port)
```

### 1. DriverReader
Opens the driver's communication port / maps the ring. Drains records, assigns the
**monotonic `seq` per server** (persisted high-water mark), converts to typed events.

```csharp
public sealed record RawEvent(long Seq, DateTimeOffset At, Operation Op, ObjectType Type,
    bool Success, int ProcessId, string Sid, string Path, string? NewPath);
```

On an overflow signal from the driver: emit an `IntegrityEvent(DriverOverflow, droppedCount)`.

### 2. Correlator
Driver gives exact renames already. Remaining logic:
- **Copy** = a read of source then a create of destination, same process, within a short
  window and equal file name -> one `Copied` action (Inferred).
- **Modified**: merge repeated writes to the same file by the same user within N seconds.
- **Delete**: driver disposition+cleanup -> one `Deleted`.
Keep short-lived state keyed by (processId, path/handle); a `SafeSeq` = lowest seq still
held, so the Batcher never checkpoints past an open correlation.

### 3. Enricher
- SID -> account name (cached AD lookup, 1h TTL).
- local path -> UNC/share path (from the server's share list, pushed by the backend).
- processId -> process name (driver provides it; cache by PID+start time).
- compute `path_hash` = 64-bit hash of lower(path)  (SAME function the DDC module uses).

### 4. Router (scope)
Keep only events inside an active session scope (server + folders + operations). The
ScopeManager also pushes the union of active scopes down to the driver so it filters early.

### 5. Batcher + Outbox
- Batch at N actions or T ms.
- Write each batch to an **encrypted on-disk outbox** (AES-GCM, key via DPAPI) before
  upload. Atomic file write (temp + rename).
- Delete a batch only after the backend acks durable storage.
- Persist `last_seq` after each ack; on restart, resume from it.

### 6. Uploader
Drains the outbox oldest-first over the control connection; on failure, retries with
back-off; stops only on shutdown.

## Normalized action (agent -> backend) — matches ClickHouse audit.actions

```csharp
public sealed record AuditAction(
  Guid TenantId, Guid ServerId, long Seq, DateTimeOffset OccurredAt,
  ObjectType ObjectType, Operation Operation, bool Success, Certainty Certainty,
  string Path, long PathHash, string? NewPath, string? ShareName,
  string ActorSid, string? ActorName, string? ClientIp, string? ProcessName,
  string? OldSecurity, string? NewSecurity, string? Details);
```

Reads ("Content View") go to a separate merged shape: one per (server, actorSid,
pathHash, hour) with a count — the agent merges before upload.

## Control channel (agent <-> backend)
Outbound long-lived connection (SignalR/gRPC), mutual-TLS in prod, API key for POC.
- Backend -> agent: StartScope, StopScope, UpdateScope, Ping, UpdateShares.
- Agent -> backend: EventBatch, ReadBatch, CommandResult, Heartbeat, IntegrityEvent.
- Commands carry id + expiry; the agent ignores duplicates by command id.
- Heartbeat every 30s: version, ring lag, dropped-count, outbox size, active scopes.

## Config (appsettings.json)
```json
{ "Agent": {
    "BackendUrl": "https://localhost:5001",
    "ApiKey": "paste-once",
    "ServerId": "GUID-of-this-server",
    "DataDirectory": "C:\\ProgramData\\Auditor\\Agent",
    "BatchMaxActions": 1000, "BatchMaxMs": 1000,
    "OutboxMaxBytes": 5368709120 } }
```

## POC
Driver via FltSendMessage; correlate create/modify/delete/rename; outbox + resume;
upload to backend. Copy/content-view optional.
