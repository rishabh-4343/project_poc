#include "scope.h"

static uint16_t ReadU16(const uint8_t* p)
{
    return (uint16_t)(p[0] | (p[1] << 8));
}

static uint32_t ReadU32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

/* Folder entries are packed, so characters are read byte-wise (no alignment assumption). */
static aud_wchar ReadChar(const uint8_t* p, size_t index)
{
    return (aud_wchar)ReadU16(p + index * 2);
}

SCOPE_STATUS ScopeValidate(
    const uint8_t* body, size_t bodyBytes,
    uint32_t* operationMask, uint32_t* folderCount,
    const uint8_t** folders, size_t* foldersBytes)
{
    const size_t fixedBytes = sizeof(AUDITOR_SET_SCOPE);
    uint32_t count;
    size_t offset;
    uint32_t i;

    if (bodyBytes < fixedBytes) {
        return SCOPE_ERR_TOO_SHORT;
    }
    if (bodyBytes > AUDITOR_MAX_SCOPE_BYTES) {
        return SCOPE_ERR_TOO_BIG;
    }

    count = ReadU32(body + 4);
    if (count > AUDITOR_MAX_FOLDERS) {
        return SCOPE_ERR_TOO_MANY;
    }

    offset = fixedBytes;
    for (i = 0; i < count; i++) {
        uint16_t pathBytes;

        if (bodyBytes - offset < 4) {
            return SCOPE_ERR_BAD_FOLDER;
        }
        pathBytes = ReadU16(body + offset + 2);
        offset += 4;

        if (pathBytes == 0 || (pathBytes & 1) != 0 || bodyBytes - offset < pathBytes) {
            return SCOPE_ERR_BAD_FOLDER;
        }
        if (ReadChar(body + offset, 0) != L'\\') {
            return SCOPE_ERR_NOT_NT_PATH;
        }
        offset += pathBytes;
    }

    if (offset != bodyBytes) {
        return SCOPE_ERR_TRAILING;
    }

    *operationMask = ReadU32(body);
    *folderCount = count;
    *folders = body + fixedBytes;
    *foldersBytes = bodyBytes - fixedBytes;
    return SCOPE_OK;
}

/* Case-insensitive equality of path[0..n) with root[0..n). */
static int PrefixEquals(const aud_wchar* path, const uint8_t* root, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++) {
        if (AudUpcase(path[i]) != AudUpcase(ReadChar(root, i))) {
            return 0;
        }
    }
    return 1;
}

int ScopeMatchPath(const uint8_t* folders, uint32_t folderCount, const aud_wchar* path, size_t pathChars)
{
    const uint8_t* p = folders;
    uint32_t i;

    for (i = 0; i < folderCount; i++) {
        uint16_t flags = ReadU16(p);
        size_t rootChars = ReadU16(p + 2) / 2u;
        const uint8_t* root = p + 4;
        p += 4 + rootChars * 2;

        /* A trailing backslash on the folder does not change what it means. */
        while (rootChars > 1 && ReadChar(root, rootChars - 1) == L'\\') {
            rootChars--;
        }

        if (pathChars < rootChars || !PrefixEquals(path, root, rootChars)) {
            continue;
        }
        if (pathChars == rootChars) {
            return 1;                       /* the folder itself */
        }
        if (path[rootChars] != L'\\') {
            continue;                       /* \Finance must not match \FinanceOld */
        }
        if (flags & AUDITOR_FOLDER_RECURSIVE) {
            return 1;
        }

        /* Not recursive: only direct children, i.e. no further separator after the folder. */
        {
            size_t k;
            int deeper = 0;
            for (k = rootChars + 1; k < pathChars; k++) {
                if (path[k] == L'\\') {
                    deeper = 1;
                    break;
                }
            }
            if (!deeper) {
                return 1;
            }
        }
    }
    return 0;
}

int ScopeHasOperation(uint32_t operationMask, uint8_t operation)
{
    if (operation != OP_OTHER && (operation < 1 || operation > 16)) {
        return 0;
    }
    return (operationMask & AUDIT_OP_BIT(operation)) != 0;
}
