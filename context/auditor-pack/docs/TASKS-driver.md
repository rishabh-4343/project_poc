# TASKS — Driver (C++ minifilter)   [owner: you]

## Context for a fresh agent
You are building a Windows kernel **minifilter driver** that captures file operations on
a file server and hands them to a user-mode C# agent. This is the `wfsa` module's capture
layer. Read `docs/00-OVERVIEW.md` and `docs/CONVENTIONS.md` first. All work lives in
`backend/driver/`. Do the tasks in order; each says when it is done. Keep kernel code
minimal — only capture + hand-off; all logic is in the agent.

Transport decision: use **FltSendMessage** (communication port) for the POC. The ring
buffer (D6) is a later optimization — do not block on it.

---
### D1 · Driver project scaffold + load/unload
- Goal: an empty signed-for-test minifilter that loads and unloads cleanly.
- Steps:
  1. Create a **Kernel Mode Driver, Filter** project `AuditorDriver` in `backend/driver/AuditorDriver/`.
  2. Implement `DriverEntry`: `FltRegisterFilter` with an empty callback array, `FltStartFiltering`.
  3. Implement `FilterUnload`, `InstanceSetup`, `InstanceQueryTeardown`.
  4. Add an INF for install. Enable test signing on the dev VM (`bcdedit /set testsigning on`, reboot).
  5. Install with `fltmc load AuditorDriver` (or the INF); confirm with `fltmc filters`.
- Creates: `backend/driver/AuditorDriver/*` (driver.c, registration, INF, vcxproj).
- Done when: `fltmc filters` lists AuditorDriver after load and it is gone after `fltmc unload`.
- If resuming: run `fltmc filters`; if AuditorDriver is absent, you are before D1 done.

### D2 · Communication port + scope handshake
- Goal: the agent can connect and send the watch scope; the driver stores it.
- Steps:
  1. `FltCreateCommunicationPort` with connect/disconnect/message callbacks; single connection.
  2. Define a message: `SET_SCOPE { pathCount; paths[]; operationMask }`. Store it in a
     driver global guarded by a lock.
  3. On message, replace the current scope; reply OK.
- Creates: `comms.c/.h`, `scope.c/.h` under the driver project.
- Done when: a tiny test user-mode exe connects via `FilterConnectCommunicationPort`,
  sends a scope, and gets OK; driver logs the stored scope (DbgPrint).

### D3 · Capture Create / Open / Open(Denied)
- Goal: emit a record for file/folder create and open, including access-denied.
- Steps:
  1. Register `PreCreate`/`PostCreate`.
  2. In `PostCreate`, read the result: disposition (created vs opened), desired access,
     `Data->IoStatus.Status` (STATUS_ACCESS_DENIED -> denied variant).
  3. Resolve path via `FltGetFileNameInformation(FLT_FILE_NAME_NORMALIZED)` + `FltParseFileNameInformation`.
  4. Determine object type (file vs directory) from the create options / file attributes.
  5. Fast-reject if path not in scope (D2) before building the record.
  6. Build an `AUDIT_RECORD` (see D5) and send it (D5 transport).
- Done when: creating and opening a file in a watched folder prints a record; opening a
  denied file prints the denied variant.

### D4 · Capture SetInformation (rename, delete, security) + Write (modify)
- Goal: exact rename/move, delete, permission/owner/auditing change, and modify.
- Steps:
  1. Register `PreSetInfo`/`PostSetInfo` and `PreWrite`.
  2. In SetInfo, switch on `FileInformationClass`:
     - `FileRenameInformation` / `FileRenameInformationEx` -> **Rename/Move with old AND new name** (both exact).
     - `FileDispositionInformation` / `...Ex` (delete flag) -> **Delete** (confirm in PostCleanup).
  3. Security changes: handle via the security callback or detect the SD-setting path;
     record Permission / Owner / Auditing change (which SD part changed).
  4. `PreWrite` -> **Modify**.
  5. Emit records as in D3.
- Done when: rename shows old+new path; delete, permission change, and modify each emit the
  right record, allowed and denied.

### D5 · Record format + transport to agent (POC = FltSendMessage)
- Goal: a stable fixed-size record the agent parses, delivered over the port.
- Steps:
  1. Define `AUDIT_RECORD` in a header **shared with the agent** (copy into agent repo):
     `version, timestamp, operation, objectType, status, processId, sid[68], pathLen, newPathLen` + inline UTF-16 path(s). `seq` is left 0 (the agent assigns it).
  2. Capture the requestor SID (thread token) and processId (`FltGetRequestorProcessId`).
  3. Send each record to the agent with `FltSendMessage`.
  4. Keep a `gDroppedCount`; if send fails/agent absent, increment and set an overflow flag.
- Creates: `audit_record.h` (shared), `emit.c`.
- Done when: the agent (A2) receives and prints records for all operations in D3/D4.

### D6 · (Post-POC) Shared ring buffer + overflow signal
- Goal: higher throughput than FltSendMessage under load.
- Steps: non-paged SPSC ring mapped to the agent; interlocked head/tail; on full, bump
  `gDroppedCount` + signal the port (do not block I/O); agent maps and drains it.
- Done when: a write-heavy test sustains throughput with no file-I/O stalls; overflow
  produces an integrity event in the agent.
