# Auditor — Frozen Contracts (driver <-> agent)

These are byte-exact agreements between two separately-built programs. Changing one side
without the other produces silent corruption, not an error. **Both files are the single
source of truth; edit them in lockstep and bump the version.**

Owner: you (both the driver and the agent are yours).

## 1. Record wire format
- Header file (C++): `audit_record.h` — the driver `#include`s it.
- C# mirror: `AuditRecord.cs` — the agent uses `AuditRecordCodec.Parse`.
- Verified: the C header compiles to exactly **32 bytes** with the offsets below; the C#
  reader parses a matching sample.

### Framing
One `FltSendMessage` = one record = a 32-byte fixed header followed by the variable
region `[sid][path][newPath]`. Total bytes = `32 + sidLen + pathLen + newPathLen`.
Little-endian. All text is **UTF-16LE**. Lengths are in **bytes**, not characters.

### Header (32 bytes)
| Offset | Field | Type | Meaning |
|---|---|---|---|
| 0 | version | u8 | AUDIT_RECORD_VERSION (currently 1) |
| 1 | operation | u8 | AuditOperation enum |
| 2 | objectType | u8 | 1 File, 2 Folder, 3 Share |
| 3 | flags | u8 | bit0 Denied, bit1 Inferred |
| 4 | processId | u32 | requestor PID |
| 8 | status | i32 | NTSTATUS; 0 = success |
| 12 | reserved0 | u32 | padding (align timestamp) |
| 16 | timestamp | u64 | Windows FILETIME (100ns since 1601 UTC) |
| 24 | sidLen | u16 | bytes of sid |
| 26 | pathLen | u16 | bytes of path |
| 28 | newPathLen | u16 | bytes of newPath (0 if none) |
| 30 | reserved1 | u16 | padding (header = 32) |

Variable region at offset 32: `sid` (sidLen bytes), then `path`, then `newPath`.

### Rules
- `seq` is NOT on the wire. The **agent** assigns a monotonic `seq` per server on receipt
  (see action identity in 00-OVERVIEW). The driver leaves nothing for it.
- `operation`/`objectType` enum numbers are fixed in both files; never renumber, only append.
- The driver sets `flags` Denied when `status != 0`. Inferred is for the agent (e.g. Copy).
- Rename/Move carry both `path` (old) and `newPath` (new), both exact.
- Versioning: change the layout -> bump `AUDIT_RECORD_VERSION` in the header AND `Version`
  in `AuditRecordCodec`. The C# reader rejects a record whose version it doesn't support,
  so a mismatched driver/agent fails loudly instead of misreading.

### Enum values (fixed)
Created=1, Modified=2, Deleted=3, Renamed=4, Moved=5, Copied=6, Open=7, Read=8,
ContentView=9, ReadBasicInfo=10, PropertyChanged=11, PermissionChange=12, OwnerChange=13,
AuditingChange=14, ShareCreated=15, ShareRemoved=16, Other=99.
ObjectType: File=1, Folder=2, Share=3.

## 2. path_hash (agent only, no cross-module dependency now)
DDC is out of scope, so `path_hash` is only an index for "one file's history" queries.
- Definition: `path_hash = xxHash64(seed 0, UTF-8 bytes of lower(local path), trailing '\' stripped)`.
- C#: `System.IO.Hashing.XxHash64`. Keep this definition even without DDC, so DDC can match later.
- It is not on the wire; the agent computes it in the Enricher.
