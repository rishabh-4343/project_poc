/* =====================================================================
 * driver_protocol.h  —  Driver <-> Agent control protocol (SHARED)
 *
 * Complements audit_record.h (which is frozen and only defines the audit
 * record itself). Everything here travels over the minifilter
 * communication port. The C# mirror is Agent/Driver/DriverProtocol.cs.
 * Little-endian, UTF-16LE text. Change only with AUDITOR_PROTOCOL_VERSION.
 *
 * PATHS ARE NT PATHS, e.g. \Device\HarddiskVolume3\Finance\a.txt — both the
 * scope sent to the driver and the paths in the records it returns. The
 * agent translates to/from drive-letter paths (D:\Finance\a.txt); the driver
 * does no translation, so it needs no per-volume state.
 * ===================================================================== */
#ifndef AUDITOR_DRIVER_PROTOCOL_H
#define AUDITOR_DRIVER_PROTOCOL_H
#include <stdint.h>
#include "audit_record.h"

#define AUDITOR_PROTOCOL_VERSION  1
#define AUDITOR_PORT_NAME         L"\\AuditorDriverPort"

/* ---- driver -> agent --------------------------------------------------
 * Every message is either an audit record (first byte = AUDIT_RECORD_VERSION,
 * which is 1) or a control notice (first byte = AUDITOR_NOTICE_MARKER). The
 * marker can never be a record version, so the agent tells them apart by the
 * first byte. */
#define AUDITOR_NOTICE_MARKER     0xF0
#define AUDITOR_NOTICE_OVERFLOW   1

#pragma pack(push, 1)
typedef struct _AUDITOR_OVERFLOW_NOTICE {
    uint8_t  marker;            /* 0  : AUDITOR_NOTICE_MARKER */
    uint8_t  kind;              /* 1  : AUDITOR_NOTICE_OVERFLOW */
    uint16_t reserved;          /* 2  */
    uint32_t protocolVersion;   /* 4  */
    uint64_t droppedTotal;      /* 8  : records the driver could not deliver since it loaded */
} AUDITOR_OVERFLOW_NOTICE;      /* 16 bytes */
#pragma pack(pop)

/* ---- agent -> driver (FilterSendMessage) -------------------------------- */
#define AUDITOR_CMD_SET_SCOPE     1   /* replace the watch scope; empty scope stops all auditing */
#define AUDITOR_CMD_GET_STATS     2

#define AUDITOR_MAX_FOLDERS       256
#define AUDITOR_MAX_FOLDER_BYTES  65534   /* path bytes, UTF-16LE, no terminator */
#define AUDITOR_MAX_SCOPE_BYTES   (1024 * 1024)

/* Operation mask: bit (op - 1) for operations 1..16, bit 31 for OP_OTHER (99). */
#define AUDIT_OP_BIT(op)  (((op) == OP_OTHER) ? 0x80000000u : (1u << (((op) - 1) & 31)))

#define AUDITOR_FOLDER_RECURSIVE  0x0001

#pragma pack(push, 1)
typedef struct _AUDITOR_COMMAND_HEADER {
    uint32_t command;           /* 0 : AUDITOR_CMD_* */
    uint32_t protocolVersion;   /* 4 : AUDITOR_PROTOCOL_VERSION */
} AUDITOR_COMMAND_HEADER;       /* 8 bytes */

/* SET_SCOPE: header, then this, then folderCount folder entries back to back:
 *   [uint16 flags][uint16 pathBytes][pathBytes of UTF-16LE NT path]
 * A folder matches itself and what is under it (everything when recursive,
 * direct children only otherwise), compared case-insensitively. */
typedef struct _AUDITOR_SET_SCOPE {
    uint32_t operationMask;     /* 0 : AUDIT_OP_BIT() flags the agent wants */
    uint32_t folderCount;       /* 4 */
} AUDITOR_SET_SCOPE;            /* 8 bytes */

/* Reply to every command. */
typedef struct _AUDITOR_REPLY {
    uint32_t protocolVersion;   /* 0  */
    uint32_t folderCount;       /* 4  : folders now in force */
    uint32_t operationMask;     /* 8  : mask now in force */
    uint32_t reserved;          /* 12 */
    uint64_t droppedTotal;      /* 16 : records dropped since load */
    uint64_t sentTotal;         /* 24 : records delivered since load */
} AUDITOR_REPLY;                /* 32 bytes */
#pragma pack(pop)

#endif /* AUDITOR_DRIVER_PROTOCOL_H */
