# Auditor — Minifilter Driver (C/C++)

The one kernel component. Keep it minimal: capture and hand off. All policy, naming,
correlation and networking live in the agent.

## Toolchain
- Visual Studio + WDK (Windows Driver Kit), matching SDK.
- Project type: **Kernel Mode Driver, Filter (minifilter)**.
- Test-sign for dev (`bcdedit /set testsigning on`); production needs EV cert +
  Microsoft attestation signing.

## Registration
Register with Filter Manager in `DriverEntry`:

```c
CONST FLT_OPERATION_REGISTRATION Callbacks[] = {
  { IRP_MJ_CREATE,          0, PreCreate,  PostCreate },   // open/create/delete-on-close
  { IRP_MJ_SET_INFORMATION, 0, PreSetInfo, PostSetInfo },  // rename, delete, disposition
  { IRP_MJ_WRITE,           0, PreWrite,   NULL },         // modify
  { IRP_MJ_CLEANUP,         0, NULL,       PostCleanup },  // handle closed
  { IRP_MJ_OPERATION_END }
};

CONST FLT_REGISTRATION FilterRegistration = {
  sizeof(FLT_REGISTRATION), FLT_REGISTRATION_VERSION, 0,
  ContextRegistration,          // stream/file contexts for correlation
  Callbacks,
  FilterUnload,
  InstanceSetup, InstanceQueryTeardown, NULL, NULL,
  NULL, NULL, NULL
};

NTSTATUS DriverEntry(PDRIVER_OBJECT drv, PUNICODE_STRING reg) {
  FltRegisterFilter(drv, &FilterRegistration, &gFilter);
  // communication port: agent connects, sends scope, receives overflow signals
  CreateCommunicationPort();            // FltCreateCommunicationPort
  FltStartFiltering(gFilter);
  return STATUS_SUCCESS;
}
```

## What each callback captures
Build one fixed-size record per operation and push it to the ring (below).

| Callback | Operation(s) | Key fields |
|---|---|---|
| PreCreate/PostCreate | Create, Open, Open(Denied), delete-on-close | desired access, disposition, status |
| PreSetInfo/PostSetInfo | Rename, Move (old+new name), Delete, security change | FileInformationClass, old+new name |
| PreWrite | Modify | — |
| Security (via FLT callback or SetInfo) | Permission/Owner/Auditing change | which SD part changed |

Resolve the full path with `FltGetFileNameInformation(... FLT_FILE_NAME_NORMALIZED ...)`.
Get the actor from `FltGetRequestorProcessId` + the thread token SID. Record
`STATUS_ACCESS_DENIED` as the denied variant.

## Record format (kernel -> agent)
Fixed-size header + inline UTF-16 path(s). No heap strings in kernel.

```c
typedef struct _AUDIT_RECORD {
  UINT64      seq;              // filled by the AGENT, not here (0 in kernel)
  UINT64      timestamp;        // KeQuerySystemTime
  UINT32      operation;        // enum
  UINT32      objectType;       // File/Folder
  UINT32      status;           // 0 = allowed, else NTSTATUS (denied)
  UINT32      processId;
  WCHAR       sid[68];          // requestor SID string
  UINT16      pathLen, newPathLen;
  // followed by path (UTF-16) then newPath (UTF-16)
} AUDIT_RECORD;
```

## Handing records to the agent — shared ring buffer (preferred)
A lock-free SPSC ring in non-paged pool, mapped into the agent. Driver is producer,
agent is consumer.

- Allocate with `ExAllocatePool2(POOL_FLAG_NON_PAGED, size, tag)`.
- Share via a section the agent maps (or `FltSendMessage` for lower rates).
- Head/tail as `volatile LONG`, advanced with `InterlockedExchange`.
- **On full ring**: do NOT block. Increment `gDroppedCount`, set an overflow flag,
  and signal the agent via the port. The agent emits an `integrity_events` row.

`FltSendMessage` alternative (simpler, lower throughput): push each record to the
agent's port; the agent replies to ack. Use for the POC, switch to the ring under load.

## Scope control (agent -> driver)
The agent sends, over the port, the set of watched paths + an operation bitmask.
In `PreCreate` etc., fast-reject anything outside scope before building a record, so
unwatched volumes cost almost nothing.

## Correlation hooks the driver should enable
- **Rename/Move**: `FileRenameInformation` in SetInfo gives old and new name in one
  operation — emit both, exact (no inference needed).
- **Delete**: `FileDispositionInformation` (marks delete) + PostCleanup confirms.
- **Copy**: the driver cannot see "copy"; it sees a read of source + create of dest.
  Leave copy detection to the agent.

## Safety rules
- Minimal work at `PASSIVE_LEVEL`; never block in a callback.
- Guard every allocation; a bugcheck is a customer bluescreen.
- Version the record struct (a `version` byte) so agent and driver can evolve.
- Unload cleanly: stop filtering, drain the ring, close the port.

## POC scope
Capture Create, Modify, Delete, Rename (exact), Permission/Owner change, and
Open(Denied). Copy and content-view can follow. Use `FltSendMessage` first; move to the
ring buffer when you test volume.
