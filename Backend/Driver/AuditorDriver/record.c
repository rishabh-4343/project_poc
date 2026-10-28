#include "record.h"

/* The header is part of the frozen contract: 32 bytes, no padding. */
typedef char AudHeaderIs32Bytes[(sizeof(AUDIT_RECORD_HEADER) == 32) ? 1 : -1];

static void PutU16(uint8_t* p, uint16_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)(v >> 8);
}

static void PutU32(uint8_t* p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)(v >> 24);
}

static void PutU64(uint8_t* p, uint64_t v)
{
    PutU32(p, (uint32_t)(v & 0xFFFFFFFFu));
    PutU32(p + 4, (uint32_t)(v >> 32));
}

/* Text goes out as UTF-16LE whatever the machine order, so each character is written as two bytes. */
static uint8_t* PutText(uint8_t* p, const aud_wchar* text, size_t chars)
{
    size_t i;
    for (i = 0; i < chars; i++) {
        PutU16(p, (uint16_t)text[i]);
        p += 2;
    }
    return p;
}

size_t AuditRecordSize(size_t sidChars, size_t pathChars, size_t newPathChars)
{
    if (sidChars > 0x7FFF || pathChars > 0x7FFF || newPathChars > 0x7FFF) {
        return 0;
    }
    return AUDIT_HEADER_SIZE + (sidChars + pathChars + newPathChars) * 2;
}

size_t AuditBuildRecord(
    uint8_t* buf, size_t cap,
    uint8_t operation, uint8_t objectType, uint8_t flags,
    uint32_t processId, int32_t status, uint64_t fileTime,
    const aud_wchar* sid, size_t sidChars,
    const aud_wchar* path, size_t pathChars,
    const aud_wchar* newPath, size_t newPathChars)
{
    size_t total = AuditRecordSize(sidChars, pathChars, newPathChars);
    uint8_t* p;

    if (total == 0 || total > cap) {
        return 0;
    }

    if (status != 0) {
        flags |= AUDIT_FLAG_DENIED;
    }

    buf[0] = AUDIT_RECORD_VERSION;
    buf[1] = operation;
    buf[2] = objectType;
    buf[3] = flags;
    PutU32(buf + 4, processId);
    PutU32(buf + 8, (uint32_t)status);
    PutU32(buf + 12, 0);
    PutU64(buf + 16, fileTime);
    PutU16(buf + 24, (uint16_t)(sidChars * 2));
    PutU16(buf + 26, (uint16_t)(pathChars * 2));
    PutU16(buf + 28, (uint16_t)(newPathChars * 2));
    PutU16(buf + 30, 0);

    p = buf + AUDIT_HEADER_SIZE;
    p = PutText(p, sid, sidChars);
    p = PutText(p, path, pathChars);
    PutText(p, newPath, newPathChars);
    return total;
}

void AuditPatchStatus(uint8_t* record, int32_t status)
{
    PutU32(record + 8, (uint32_t)status);
    if (status != 0) {
        record[3] |= AUDIT_FLAG_DENIED;
    } else {
        record[3] &= (uint8_t)~AUDIT_FLAG_DENIED;
    }
}
