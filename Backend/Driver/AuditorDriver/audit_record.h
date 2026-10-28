/* =====================================================================
 * audit_record.h  —  Driver <-> Agent wire contract (SHARED, FROZEN)
 * The C++ minifilter #includes this. The C# agent mirrors it exactly
 * (see AuditRecord.cs). Change only by bumping AUDIT_RECORD_VERSION and
 * updating BOTH sides. Little-endian (x86/x64). Paths are UTF-16LE.
 * One FltSendMessage carries exactly one record: a 32-byte fixed header
 * followed by the variable region (sid, path, newPath), in that order.
 * Total bytes = 32 + sidLen + pathLen + newPathLen.
 * ===================================================================== */
#ifndef AUDITOR_AUDIT_RECORD_H
#define AUDITOR_AUDIT_RECORD_H
#include <stdint.h>

#define AUDIT_RECORD_VERSION  1

/* operation codes (u8) */
enum AuditOperation {
    OP_CREATED           = 1,
    OP_MODIFIED          = 2,
    OP_DELETED           = 3,
    OP_RENAMED           = 4,
    OP_MOVED             = 5,
    OP_COPIED            = 6,   /* agent-inferred; driver rarely emits */
    OP_OPEN              = 7,
    OP_READ              = 8,
    OP_CONTENT_VIEW      = 9,
    OP_READ_BASIC_INFO   = 10,
    OP_PROPERTY_CHANGED  = 11,
    OP_PERMISSION_CHANGE = 12,
    OP_OWNER_CHANGE      = 13,
    OP_AUDITING_CHANGE   = 14,
    OP_SHARE_CREATED     = 15,
    OP_SHARE_REMOVED     = 16,
    OP_OTHER             = 99
};

/* object type (u8) */
enum AuditObjectType {
    OBJ_FILE   = 1,
    OBJ_FOLDER = 2,
    OBJ_SHARE  = 3
};

/* flags (u8, bitmask) */
#define AUDIT_FLAG_DENIED    0x01   /* access was denied (status != 0) */
#define AUDIT_FLAG_INFERRED  0x02   /* operation inferred, not directly observed */

/* 32-byte fixed header. #pragma pack to guarantee no compiler padding. */
#pragma pack(push, 1)
typedef struct _AUDIT_RECORD_HEADER {
    uint8_t  version;        /* 0  : AUDIT_RECORD_VERSION */
    uint8_t  operation;      /* 1  : enum AuditOperation */
    uint8_t  objectType;     /* 2  : enum AuditObjectType */
    uint8_t  flags;          /* 3  : AUDIT_FLAG_* */
    uint32_t processId;      /* 4  : requestor PID */
    int32_t  status;         /* 8  : NTSTATUS (0 = success) */
    uint32_t reserved0;      /* 12 : pad -> 8-byte align timestamp */
    uint64_t timestamp;      /* 16 : Windows FILETIME (100ns since 1601 UTC) */
    uint16_t sidLen;         /* 24 : bytes of sid (UTF-16LE) in the variable region */
    uint16_t pathLen;        /* 26 : bytes of path (UTF-16LE) */
    uint16_t newPathLen;     /* 28 : bytes of newPath (UTF-16LE); 0 if none */
    uint16_t reserved1;      /* 30 : pad -> header size 32 */
    /* variable region follows at offset 32:
     *   [sid : sidLen bytes][path : pathLen bytes][newPath : newPathLen bytes]
     * seq is NOT here — the agent assigns it on receipt. */
} AUDIT_RECORD_HEADER;
#pragma pack(pop)

#define AUDIT_HEADER_SIZE  ((uint32_t)sizeof(AUDIT_RECORD_HEADER))  /* must be 32 */

#endif /* AUDITOR_AUDIT_RECORD_H */
