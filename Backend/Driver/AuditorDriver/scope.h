/* scope.h — the watch scope: validation of the SET_SCOPE message and path matching.
 * Portable: no kernel calls, so it is unit-tested in user mode. The stored form of the folders is the wire
 * form (packed entries), so the driver copies the validated message region as is. */
#ifndef AUDITOR_SCOPE_H
#define AUDITOR_SCOPE_H

#include "platform.h"
#include "driver_protocol.h"

typedef enum _SCOPE_STATUS {
    SCOPE_OK = 0,
    SCOPE_ERR_TOO_SHORT,        /* message shorter than its headers */
    SCOPE_ERR_VERSION,          /* protocolVersion mismatch */
    SCOPE_ERR_TOO_MANY,         /* more than AUDITOR_MAX_FOLDERS */
    SCOPE_ERR_TOO_BIG,          /* more than AUDITOR_MAX_SCOPE_BYTES */
    SCOPE_ERR_BAD_FOLDER,       /* a folder entry is empty, odd-sized, or runs past the message */
    SCOPE_ERR_TRAILING,         /* bytes left over after the last folder */
    SCOPE_ERR_NOT_NT_PATH       /* a folder path does not start with a backslash */
} SCOPE_STATUS;

/* Validates the SET_SCOPE body (everything after AUDITOR_COMMAND_HEADER). On success, *folders points at the
 * first folder entry inside `body` and *foldersBytes is their total size. */
SCOPE_STATUS ScopeValidate(
    const uint8_t* body, size_t bodyBytes,
    uint32_t* operationMask, uint32_t* folderCount,
    const uint8_t** folders, size_t* foldersBytes);

/* True when `path` (UTF-16, `pathChars` characters, not terminated) is inside any of the validated folders. */
int ScopeMatchPath(const uint8_t* folders, uint32_t folderCount, const aud_wchar* path, size_t pathChars);

/* True when the mask contains the operation. */
int ScopeHasOperation(uint32_t operationMask, uint8_t operation);

#endif /* AUDITOR_SCOPE_H */
