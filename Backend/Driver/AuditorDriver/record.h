/* record.h — builds one audit record exactly as laid out in audit_record.h (32-byte header, then
 * sid, path, newPath as UTF-16LE). Portable and little-endian by construction (bytes are written one at a time),
 * so the user-mode tests can check the very code the driver runs. */
#ifndef AUDITOR_RECORD_H
#define AUDITOR_RECORD_H

#include "platform.h"
#include "audit_record.h"

/* Total bytes of a record, or 0 when a text would not fit its 16-bit length field. */
size_t AuditRecordSize(size_t sidChars, size_t pathChars, size_t newPathChars);

/* Writes a record into buf (capacity cap). Returns the bytes written, or 0 when it does not fit.
 * The DENIED flag is set whenever status != 0, as the contract requires. Texts may be NULL when their length is 0. */
size_t AuditBuildRecord(
    uint8_t* buf, size_t cap,
    uint8_t operation, uint8_t objectType, uint8_t flags,
    uint32_t processId, int32_t status, uint64_t fileTime,
    const aud_wchar* sid, size_t sidChars,
    const aud_wchar* path, size_t pathChars,
    const aud_wchar* newPath, size_t newPathChars);

/* Sets the outcome of an already built record (used when the result is only known after the operation). */
void AuditPatchStatus(uint8_t* record, int32_t status);

#endif /* AUDITOR_RECORD_H */
