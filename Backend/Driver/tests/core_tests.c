/* User-mode checks of the driver's portable core (scope.c, record.c): the exact code the driver runs.
 * Usage: core_tests [samples.bin]   — with a path it also writes length-prefixed records for the C# parser to read back. */
#pragma warning(disable : 4127)   /* the CHECK macro is a constant-condition do/while */
#include <stdio.h>
#include <stdlib.h>
#include "../AuditorDriver/scope.h"
#include "../AuditorDriver/record.h"

static int failures;
static int checks;

#define CHECK(name, cond) do { checks++; if (cond) { printf("  PASS  %s\n", name); } else { failures++; printf("  FAIL  %s  (line %d)\n", name, __LINE__); } } while (0)

/* ---- helpers to build a SET_SCOPE body ------------------------------------------------------------- */
static uint8_t body[4096];
static size_t bodyLen;

static void PutU16(size_t at, uint16_t v) { body[at] = (uint8_t)(v & 0xFF); body[at + 1] = (uint8_t)(v >> 8); }
static void PutU32(size_t at, uint32_t v) { PutU16(at, (uint16_t)(v & 0xFFFF)); PutU16(at + 2, (uint16_t)(v >> 16)); }

static void BeginScope(uint32_t mask)
{
    memset(body, 0, sizeof(body));
    PutU32(0, mask);
    PutU32(4, 0);
    bodyLen = 8;
}

static void AddFolder(const wchar_t* path, int recursive)
{
    size_t chars = wcslen(path);
    size_t i;
    PutU16(bodyLen, recursive ? AUDITOR_FOLDER_RECURSIVE : 0);
    PutU16(bodyLen + 2, (uint16_t)(chars * 2));
    for (i = 0; i < chars; i++) {
        PutU16(bodyLen + 4 + i * 2, (uint16_t)path[i]);
    }
    bodyLen += 4 + chars * 2;
    PutU32(4, (uint32_t)(body[4] | (body[5] << 8)) + 1);
}

static SCOPE_STATUS Validate(uint32_t* mask, uint32_t* count, const uint8_t** folders)
{
    size_t bytes;
    return ScopeValidate(body, bodyLen, mask, count, folders, &bytes);
}

static int Matches(const wchar_t* path)
{
    uint32_t mask, count;
    const uint8_t* folders;
    if (Validate(&mask, &count, &folders) != SCOPE_OK) {
        return -1;
    }
    return ScopeMatchPath(folders, count, path, wcslen(path));
}

static uint32_t ReadU32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint16_t ReadU16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }

static void WriteSample(FILE* f, const uint8_t* rec, size_t len)
{
    uint32_t n = (uint32_t)len;
    fwrite(&n, 4, 1, f);
    fwrite(rec, 1, len, f);
}

int main(int argc, char** argv)
{
    uint32_t mask, count;
    const uint8_t* folders;

    printf("== contract\n");
    CHECK("header is 32 bytes", AUDIT_HEADER_SIZE == 32);
    CHECK("operation bits: Created is bit 0", AUDIT_OP_BIT(OP_CREATED) == 1u);
    CHECK("operation bits: Deleted is bit 2", AUDIT_OP_BIT(OP_DELETED) == 4u);
    CHECK("operation bits: ShareRemoved is bit 15", AUDIT_OP_BIT(OP_SHARE_REMOVED) == 0x8000u);
    CHECK("operation bits: Other is bit 31", AUDIT_OP_BIT(OP_OTHER) == 0x80000000u);
    CHECK("the control marker is not a record version", AUDITOR_NOTICE_MARKER != AUDIT_RECORD_VERSION);
    CHECK("overflow notice is 16 bytes", sizeof(AUDITOR_OVERFLOW_NOTICE) == 16);
    CHECK("reply is 32 bytes", sizeof(AUDITOR_REPLY) == 32);
    CHECK("command header is 8 bytes", sizeof(AUDITOR_COMMAND_HEADER) == 8);

    printf("== scope validation\n");
    BeginScope(AUDIT_OP_BIT(OP_CREATED) | AUDIT_OP_BIT(OP_DELETED));
    AddFolder(L"\\Device\\HarddiskVolume3\\Finance", 1);
    AddFolder(L"\\Device\\HarddiskVolume3\\HR\\", 0);
    CHECK("a good scope validates", Validate(&mask, &count, &folders) == SCOPE_OK && count == 2 && mask == 5u);
    BeginScope(0);
    CHECK("an empty scope is valid (it clears auditing)", Validate(&mask, &count, &folders) == SCOPE_OK && count == 0);
    CHECK("a body shorter than its header is refused", ScopeValidate(body, 4, &mask, &count, &folders, &(size_t){0}) == SCOPE_ERR_TOO_SHORT);
    BeginScope(1);
    AddFolder(L"\\a", 0);
    bodyLen -= 2;
    CHECK("a folder running past the message is refused", Validate(&mask, &count, &folders) == SCOPE_ERR_BAD_FOLDER);
    BeginScope(1);
    AddFolder(L"\\a", 0);
    bodyLen += 1;
    CHECK("trailing bytes are refused", Validate(&mask, &count, &folders) == SCOPE_ERR_TRAILING);
    BeginScope(1);
    PutU32(4, 1);
    PutU16(8, 0);
    PutU16(10, 0);
    bodyLen = 12;
    CHECK("an empty folder path is refused", Validate(&mask, &count, &folders) == SCOPE_ERR_BAD_FOLDER);
    BeginScope(1);
    PutU32(4, 1);
    PutU16(8, 0);
    PutU16(10, 3);
    bodyLen = 15;
    CHECK("an odd-sized path is refused", Validate(&mask, &count, &folders) == SCOPE_ERR_BAD_FOLDER);
    BeginScope(1);
    AddFolder(L"D:\\Finance", 1);
    CHECK("a drive-letter path is refused (the driver speaks NT paths)", Validate(&mask, &count, &folders) == SCOPE_ERR_NOT_NT_PATH);
    BeginScope(1);
    PutU32(4, AUDITOR_MAX_FOLDERS + 1);
    CHECK("too many folders are refused", Validate(&mask, &count, &folders) == SCOPE_ERR_TOO_MANY);
    BeginScope(1);
    PutU32(4, 5);
    CHECK("a count larger than the data is refused", Validate(&mask, &count, &folders) == SCOPE_ERR_BAD_FOLDER);

    printf("== path matching\n");
    BeginScope(1);
    AddFolder(L"\\Device\\HarddiskVolume3\\Finance\\", 1);
    AddFolder(L"\\Device\\HarddiskVolume3\\HR", 0);
    CHECK("the folder itself matches", Matches(L"\\Device\\HarddiskVolume3\\Finance") == 1);
    CHECK("a trailing slash on the folder is ignored", Matches(L"\\Device\\HarddiskVolume3\\Finance\\a.txt") == 1);
    CHECK("recursive: a deep file matches", Matches(L"\\Device\\HarddiskVolume3\\Finance\\2026\\Q1\\a.xlsx") == 1);
    CHECK("matching ignores case", Matches(L"\\DEVICE\\harddiskvolume3\\FINANCE\\A.TXT") == 1);
    CHECK("a sibling with the same prefix does not match", Matches(L"\\Device\\HarddiskVolume3\\FinanceOld\\a.txt") == 0);
    CHECK("another volume does not match", Matches(L"\\Device\\HarddiskVolume4\\Finance\\a.txt") == 0);
    CHECK("a shorter path does not match", Matches(L"\\Device\\HarddiskVolume3") == 0);
    CHECK("non-recursive: a direct child matches", Matches(L"\\Device\\HarddiskVolume3\\HR\\a.txt") == 1);
    CHECK("non-recursive: a grandchild does not", Matches(L"\\Device\\HarddiskVolume3\\HR\\sub\\a.txt") == 0);
    CHECK("non-recursive: the folder itself matches", Matches(L"\\Device\\HarddiskVolume3\\HR") == 1);
    CHECK("an alternate data stream is under its file", Matches(L"\\Device\\HarddiskVolume3\\Finance\\a.txt:Zone.Identifier") == 1);
    BeginScope(1);
    CHECK("an empty scope matches nothing", Matches(L"\\Device\\HarddiskVolume3\\Finance\\a.txt") == 0);
    BeginScope(1);
    AddFolder(L"\\Device\\HarddiskVolume3", 1);
    CHECK("a whole-volume scope matches the volume root's children", Matches(L"\\Device\\HarddiskVolume3\\x") == 1);
    CHECK("operation present in the mask", ScopeHasOperation(5u, OP_CREATED) == 1 && ScopeHasOperation(5u, OP_DELETED) == 1);
    CHECK("operation absent from the mask", ScopeHasOperation(5u, OP_MODIFIED) == 0 && ScopeHasOperation(5u, OP_OTHER) == 0);
    CHECK("an out-of-range operation is never in the mask", ScopeHasOperation(0xFFFFFFFFu, 50) == 0 && ScopeHasOperation(0xFFFFFFFFu, 0) == 0);

    printf("== record encoding\n");
    {
        uint8_t rec[512];
        const wchar_t* sid = L"S-1-5-21-1-2-3-1001";
        const wchar_t* path = L"\\Device\\HarddiskVolume3\\Finance\\a.txt";
        const wchar_t* newPath = L"\\Device\\HarddiskVolume3\\Finance\\b.txt";
        size_t sc = wcslen(sid), pc = wcslen(path), nc = wcslen(newPath);
        size_t n = AuditBuildRecord(rec, sizeof(rec), OP_RENAMED, OBJ_FILE, 0, 4242, 0, 0x01DC00000000ABCDull, sid, sc, path, pc, newPath, nc);

        CHECK("size is header + 2 * (sid + path + newPath)", n == 32 + 2 * (sc + pc + nc) && n == AuditRecordSize(sc, pc, nc));
        CHECK("version, operation, type, flags", rec[0] == 1 && rec[1] == OP_RENAMED && rec[2] == OBJ_FILE && rec[3] == 0);
        CHECK("process id is little-endian at 4", ReadU32(rec + 4) == 4242);
        CHECK("status at 8, reserved0 at 12", ReadU32(rec + 8) == 0 && ReadU32(rec + 12) == 0);
        CHECK("timestamp at 16", ReadU32(rec + 16) == 0x0000ABCDu && ReadU32(rec + 20) == 0x01DC0000u);
        CHECK("lengths are in bytes at 24/26/28", ReadU16(rec + 24) == sc * 2 && ReadU16(rec + 26) == pc * 2 && ReadU16(rec + 28) == nc * 2 && ReadU16(rec + 30) == 0);
        CHECK("variable region: sid, path, newPath in order", ReadU16(rec + 32) == L'S' && ReadU16(rec + 32 + sc * 2) == L'\\' && ReadU16(rec + 32 + (sc + pc) * 2 + (nc - 1) * 2) == L't');

        n = AuditBuildRecord(rec, sizeof(rec), OP_OPEN, OBJ_FOLDER, 0, 7, (int32_t)0xC0000022, 1, sid, sc, path, pc, NULL, 0);
        CHECK("a non-zero status sets the DENIED flag", rec[3] == AUDIT_FLAG_DENIED && ReadU32(rec + 8) == 0xC0000022u);
        CHECK("no newPath means length 0", ReadU16(rec + 28) == 0 && n == 32 + 2 * (sc + pc));

        n = AuditBuildRecord(rec, sizeof(rec), OP_MODIFIED, OBJ_FILE, AUDIT_FLAG_INFERRED, 7, 0, 1, sid, sc, path, pc, NULL, 0);
        CHECK("caller flags are kept", rec[3] == AUDIT_FLAG_INFERRED);
        AuditPatchStatus(rec, (int32_t)0xC0000022);
        CHECK("patching a status adds DENIED and keeps other flags", rec[3] == (AUDIT_FLAG_INFERRED | AUDIT_FLAG_DENIED) && ReadU32(rec + 8) == 0xC0000022u);
        AuditPatchStatus(rec, 0);
        CHECK("patching it back to success clears DENIED", rec[3] == AUDIT_FLAG_INFERRED && ReadU32(rec + 8) == 0);

        CHECK("a record that does not fit is refused", AuditBuildRecord(rec, 40, OP_OPEN, OBJ_FILE, 0, 1, 0, 1, sid, sc, path, pc, NULL, 0) == 0);
        CHECK("a text over the 16-bit length is refused", AuditRecordSize(0, 0x8000, 0) == 0 && AuditRecordSize(0, 0x7FFF, 0) == 32 + 0xFFFE);
    }

    if (argc > 1) {
        FILE* f = fopen(argv[1], "wb");
        uint8_t rec[512];
        size_t n;
        if (!f) {
            printf("cannot write %s\n", argv[1]);
            return 2;
        }
        n = AuditBuildRecord(rec, sizeof(rec), OP_RENAMED, OBJ_FILE, 0, 4242, 0, 133800000000000000ull, L"S-1-5-21-9-8-7-1001", 19,
            L"\\Device\\HarddiskVolume3\\Finance\\a.txt", 37, L"\\Device\\HarddiskVolume3\\Finance\\b.txt", 37);
        WriteSample(f, rec, n);
        n = AuditBuildRecord(rec, sizeof(rec), OP_OPEN, OBJ_FOLDER, 0, 7, (int32_t)0xC0000022, 133800000000000001ull, L"S-1-5-18", 8,
            L"\\Device\\HarddiskVolume3\\HR", 26, NULL, 0);
        WriteSample(f, rec, n);
        n = AuditBuildRecord(rec, sizeof(rec), OP_PERMISSION_CHANGE, OBJ_FILE, 0, 4, 0, 133800000000000002ull, L"S-1-5-32-544", 12,
            L"\\Device\\HarddiskVolume3\\Finance\\caf\x00e9.txt", 40, NULL, 0);   /* 31 chars + 9 for the file name */
        WriteSample(f, rec, n);
        fclose(f);
        printf("wrote samples to %s\n", argv[1]);
    }

    printf("%s: %d checks, %d failed\n", failures ? "FAILED" : "ALL PASSED", checks, failures);
    return failures ? 1 : 0;
}
