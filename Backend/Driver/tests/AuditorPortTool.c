/* AuditorPortTool.c — user-mode test tool for the AuditorDriver communication port (run it inside the TEST VM).
 *
 *   AuditorPortTool selftest                       D2: connect, send/replace/clear a scope, check replies, reject bad messages
 *   AuditorPortTool set  [--ops ...] [--direct] <folder>...   replace the scope and print the reply
 *   AuditorPortTool clear                          empty scope: stops all auditing
 *   AuditorPortTool stats                          print the driver's counters
 *   AuditorPortTool watch [--seconds N] [--ops ...] [--direct] <folder>...
 *                                                  D3-D5: set the scope, print every record and notice until N seconds
 *                                                  (default 60) or Ctrl+C, then clear the scope
 *
 * Folders may be NT paths (\Device\HarddiskVolume3\Finance) or drive paths (D:\Finance, translated here, as the agent does).
 * --ops is a number (0x...), "all", or "default" (the default set). --direct watches direct children only (not recursive).
 * The port allows one connection, so stop any agent first. */
#include <windows.h>
#include <fltuser.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "driver_protocol.h"

#pragma comment(lib, "fltlib.lib")

#define MSG_BODY_BYTES  (70 * 1024)
#define READER_THREADS  4

typedef struct _MESSAGE {
    FILTER_MESSAGE_HEADER Header;
    UCHAR                 Body[MSG_BODY_BYTES];
} MESSAGE;

static HANDLE           gPort;
static volatile LONG    gStop;
static CRITICAL_SECTION gPrint;
static volatile LONG    gRecords;
static volatile LONG    gNotices;
static int              gChecks;
static int              gFailed;

static const char* OpName(unsigned op)
{
    switch (op) {
    case OP_CREATED: return "Created";           case OP_MODIFIED: return "Modified";
    case OP_DELETED: return "Deleted";           case OP_RENAMED: return "Renamed";
    case OP_MOVED: return "Moved";               case OP_COPIED: return "Copied";
    case OP_OPEN: return "Open";                 case OP_READ: return "Read";
    case OP_CONTENT_VIEW: return "ContentView";  case OP_READ_BASIC_INFO: return "ReadBasicInfo";
    case OP_PROPERTY_CHANGED: return "PropertyChanged";
    case OP_PERMISSION_CHANGE: return "PermissionChange";
    case OP_OWNER_CHANGE: return "OwnerChange";  case OP_AUDITING_CHANGE: return "AuditingChange";
    case OP_SHARE_CREATED: return "ShareCreated"; case OP_SHARE_REMOVED: return "ShareRemoved";
    default: return "Other";
    }
}

static UINT32 DefaultOps(void)
{
    return AUDIT_OP_BIT(OP_CREATED) | AUDIT_OP_BIT(OP_MODIFIED) | AUDIT_OP_BIT(OP_DELETED) | AUDIT_OP_BIT(OP_RENAMED) |
           AUDIT_OP_BIT(OP_MOVED) | AUDIT_OP_BIT(OP_OPEN) | AUDIT_OP_BIT(OP_PERMISSION_CHANGE) | AUDIT_OP_BIT(OP_OWNER_CHANGE) |
           AUDIT_OP_BIT(OP_AUDITING_CHANGE);
}

static void Check(const char* what, int ok)
{
    gChecks++;
    if (!ok) {
        gFailed++;
    }
    printf("  %s  %s\n", ok ? "PASS" : "FAIL", what);
}

static int Connect(void)
{
    HRESULT hr = FilterConnectCommunicationPort(AUDITOR_PORT_NAME, 0, NULL, 0, NULL, &gPort);
    if (FAILED(hr)) {
        printf("cannot connect to %ls (0x%08lx): is the driver loaded (fltmc load AuditorDriver), and is the agent stopped?\n",
               AUDITOR_PORT_NAME, (unsigned long)hr);
        return 0;
    }
    return 1;
}

/* ---- folders ----------------------------------------------------------------------------------------------------- */

static int ToNtPath(const char* arg, WCHAR* out, size_t outChars)
{
    WCHAR wide[1024];
    if (MultiByteToWideChar(CP_ACP, 0, arg, -1, wide, 1024) == 0) {
        return 0;
    }
    if (wide[0] == L'\\') {
        wcsncpy_s(out, outChars, wide, _TRUNCATE);
        return 1;
    }
    if (wide[1] == L':') {
        WCHAR drive[3] = { wide[0], L':', 0 };
        WCHAR device[512];
        if (QueryDosDeviceW(drive, device, 512) == 0) {
            return 0;
        }
        swprintf_s(out, outChars, L"%s%s", device, wide + 2);
        return 1;
    }
    return 0;
}

typedef struct _SCOPE_MSG {
    UCHAR   Bytes[64 * 1024];
    size_t  Length;
} SCOPE_MSG;

static void PutU16(UCHAR* p, UINT16 v) { p[0] = (UCHAR)(v & 0xFF); p[1] = (UCHAR)(v >> 8); }
static void PutU32(UCHAR* p, UINT32 v) { PutU16(p, (UINT16)(v & 0xFFFF)); PutU16(p + 2, (UINT16)(v >> 16)); }

static int BuildScope(SCOPE_MSG* msg, UINT32 command, UINT32 mask, int recursive, WCHAR** folders, int count)
{
    UCHAR* p = msg->Bytes;
    int i;

    PutU32(p, command);
    PutU32(p + 4, AUDITOR_PROTOCOL_VERSION);
    p += sizeof(AUDITOR_COMMAND_HEADER);
    if (command == AUDITOR_CMD_SET_SCOPE) {
        PutU32(p, mask);
        PutU32(p + 4, (UINT32)count);
        p += sizeof(AUDITOR_SET_SCOPE);
        for (i = 0; i < count; i++) {
            size_t bytes = wcslen(folders[i]) * sizeof(WCHAR);
            if ((size_t)(p - msg->Bytes) + 4 + bytes > sizeof(msg->Bytes)) {
                return 0;
            }
            PutU16(p, recursive ? AUDITOR_FOLDER_RECURSIVE : 0);
            PutU16(p + 2, (UINT16)bytes);
            memcpy(p + 4, folders[i], bytes);
            p += 4 + bytes;
        }
    }
    msg->Length = (size_t)(p - msg->Bytes);
    return 1;
}

static HRESULT Send(const UCHAR* bytes, size_t length, AUDITOR_REPLY* reply)
{
    DWORD returned = 0;
    memset(reply, 0, sizeof(*reply));
    return FilterSendMessage(gPort, (LPVOID)bytes, (DWORD)length, reply, sizeof(*reply), &returned);
}

static void PrintReply(const char* label, HRESULT hr, const AUDITOR_REPLY* reply)
{
    if (FAILED(hr)) {
        printf("%s: failed 0x%08lx\n", label, (unsigned long)hr);
        return;
    }
    printf("%s: OK  folders=%lu mask=0x%08lx sent=%llu dropped=%llu\n", label, (unsigned long)reply->folderCount,
           (unsigned long)reply->operationMask, (unsigned long long)reply->sentTotal, (unsigned long long)reply->droppedTotal);
}

static int ParseScopeArgs(int argc, char** argv, int start, UINT32* mask, int* recursive, int* seconds, WCHAR folders[][1024], WCHAR** list)
{
    int count = 0;
    int i;

    *mask = DefaultOps();
    *recursive = 1;
    *seconds = 60;

    for (i = start; i < argc; i++) {
        if (strcmp(argv[i], "--ops") == 0 && i + 1 < argc) {
            i++;
            if (strcmp(argv[i], "all") == 0) *mask = 0xFFFFFFFFu;
            else if (strcmp(argv[i], "default") == 0) *mask = DefaultOps();
            else *mask = (UINT32)strtoul(argv[i], NULL, 0);
        } else if (strcmp(argv[i], "--direct") == 0) {
            *recursive = 0;
        } else if (strcmp(argv[i], "--seconds") == 0 && i + 1 < argc) {
            *seconds = atoi(argv[++i]);
        } else if (count < 16) {
            if (!ToNtPath(argv[i], folders[count], 1024)) {
                printf("cannot use folder '%s' (use an NT path or an existing drive path)\n", argv[i]);
                return -1;
            }
            list[count] = folders[count];
            count++;
        }
    }
    return count;
}

/* ---- commands ---------------------------------------------------------------------------------------------------- */

static int CmdSetClearStats(const char* command, int argc, char** argv)
{
    static WCHAR folders[16][1024];
    WCHAR* list[16];
    SCOPE_MSG* msg = (SCOPE_MSG*)calloc(1, sizeof(SCOPE_MSG));
    AUDITOR_REPLY reply;
    UINT32 mask;
    int recursive, seconds, count = 0;
    HRESULT hr;

    if (msg == NULL) return 2;
    if (strcmp(command, "set") == 0) {
        count = ParseScopeArgs(argc, argv, 2, &mask, &recursive, &seconds, folders, list);
        if (count < 0) { free(msg); return 2; }
        BuildScope(msg, AUDITOR_CMD_SET_SCOPE, mask, recursive, list, count);
    } else if (strcmp(command, "clear") == 0) {
        BuildScope(msg, AUDITOR_CMD_SET_SCOPE, 0, 1, NULL, 0);
    } else {
        BuildScope(msg, AUDITOR_CMD_GET_STATS, 0, 1, NULL, 0);
    }

    if (!Connect()) { free(msg); return 1; }
    hr = Send(msg->Bytes, msg->Length, &reply);
    PrintReply(command, hr, &reply);
    CloseHandle(gPort);
    free(msg);
    return FAILED(hr) ? 1 : 0;
}

static int CmdSelfTest(void)
{
    static WCHAR a[] = L"\\Device\\HarddiskVolume9\\Finance";
    static WCHAR b[] = L"\\Device\\HarddiskVolume9\\Hr";
    WCHAR* two[2] = { a, b };
    SCOPE_MSG* msg = (SCOPE_MSG*)calloc(1, sizeof(SCOPE_MSG));
    AUDITOR_REPLY reply;
    HRESULT hr;
    UCHAR junk[4] = { 1, 2, 3, 4 };
    UINT32 ver = AUDITOR_PROTOCOL_VERSION + 1;

    if (msg == NULL || !Connect()) { free(msg); return 1; }

    printf("D2 self-test (scope is cleared at the end)\n");

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, two, 2);
    hr = Send(msg->Bytes, msg->Length, &reply);
    PrintReply("set 2 folders", hr, &reply);
    Check("a valid scope is accepted", SUCCEEDED(hr));
    Check("reply: 2 folders, the mask we sent", reply.folderCount == 2 && reply.operationMask == DefaultOps());
    Check("reply carries the protocol version", reply.protocolVersion == AUDITOR_PROTOCOL_VERSION);

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, two, 1);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("a new scope replaces the old one (1 folder)", SUCCEEDED(hr) && reply.folderCount == 1);

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, NULL, 0);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("an empty scope is accepted and leaves 0 folders", SUCCEEDED(hr) && reply.folderCount == 0);

    hr = Send(junk, sizeof(junk), &reply);
    Check("a message shorter than its header is refused", FAILED(hr));

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, two, 1);
    PutU32(msg->Bytes + 4, ver);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("a wrong protocol version is refused", FAILED(hr));

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, two, 1);
    msg->Length -= 2;
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("a truncated folder entry is refused", FAILED(hr));

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, DefaultOps(), 1, two, 1);
    PutU32(msg->Bytes + 8, 0);
    PutU32(msg->Bytes + 12, 2);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("a folder count larger than the message is refused", FAILED(hr));

    BuildScope(msg, AUDITOR_CMD_GET_STATS, 0, 1, NULL, 0);
    PutU32(msg->Bytes, 99);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("an unknown command is refused", FAILED(hr));

    BuildScope(msg, AUDITOR_CMD_GET_STATS, 0, 1, NULL, 0);
    hr = Send(msg->Bytes, msg->Length, &reply);
    Check("GET_STATS still answers and shows the scope cleared after the failures", SUCCEEDED(hr) && reply.folderCount == 0);

    {
        HANDLE second = NULL;
        HRESULT hr2 = FilterConnectCommunicationPort(AUDITOR_PORT_NAME, 0, NULL, 0, NULL, &second);
        Check("a second connection is refused (single agent)", FAILED(hr2));
        if (second != NULL) CloseHandle(second);
    }

    CloseHandle(gPort);
    free(msg);
    printf("%d checks, %d failed\n", gChecks, gFailed);
    return gFailed ? 1 : 0;
}

/* ---- watch: print every record ------------------------------------------------------------------------------------ */

static void PrintText(const UCHAR* p, UINT16 bytes)
{
    printf("%.*ls", (int)(bytes / 2), (const WCHAR*)p);
}

static void PrintMessage(const UCHAR* m, DWORD length)
{
    FILETIME ft;
    SYSTEMTIME utc, local;
    UINT64 stamp;
    UINT16 sidLen, pathLen, newLen;
    const UCHAR* v;

    if (length >= 1 && m[0] == AUDITOR_NOTICE_MARKER) {
        if (length >= sizeof(AUDITOR_OVERFLOW_NOTICE)) {
            const AUDITOR_OVERFLOW_NOTICE* n = (const AUDITOR_OVERFLOW_NOTICE*)m;
            EnterCriticalSection(&gPrint);
            printf("NOTICE  overflow: the driver has dropped %llu record(s) so far\n", (unsigned long long)n->droppedTotal);
            LeaveCriticalSection(&gPrint);
            InterlockedIncrement(&gNotices);
        }
        return;
    }
    if (length < AUDIT_HEADER_SIZE || m[0] != AUDIT_RECORD_VERSION) {
        EnterCriticalSection(&gPrint);
        printf("?? unrecognised message, %lu bytes, first byte 0x%02x\n", (unsigned long)length, length ? m[0] : 0);
        LeaveCriticalSection(&gPrint);
        return;
    }

    memcpy(&stamp, m + 16, 8);
    memcpy(&sidLen, m + 24, 2);
    memcpy(&pathLen, m + 26, 2);
    memcpy(&newLen, m + 28, 2);
    if (AUDIT_HEADER_SIZE + (size_t)sidLen + pathLen + newLen > length) {
        printf("?? truncated record\n");
        return;
    }
    ft.dwLowDateTime = (DWORD)(stamp & 0xFFFFFFFFu);
    ft.dwHighDateTime = (DWORD)(stamp >> 32);
    FileTimeToSystemTime(&ft, &utc);
    SystemTimeToTzSpecificLocalTime(NULL, &utc, &local);
    v = m + AUDIT_HEADER_SIZE;

    EnterCriticalSection(&gPrint);
    printf("%02u:%02u:%02u.%03u  pid=%-6lu %-16s %-6s %s status=0x%08lx  sid=",
           local.wHour, local.wMinute, local.wSecond, local.wMilliseconds,
           (unsigned long)*(const UINT32*)(m + 4), OpName(m[1]), m[2] == OBJ_FOLDER ? "folder" : m[2] == OBJ_SHARE ? "share" : "file",
           (m[3] & AUDIT_FLAG_DENIED) ? "DENIED " : "allowed", (unsigned long)*(const UINT32*)(m + 8));
    PrintText(v, sidLen);
    printf("\n                    path: ");
    PrintText(v + sidLen, pathLen);
    if (newLen) {
        printf("\n                 newPath: ");
        PrintText(v + sidLen + pathLen, newLen);
    }
    printf("\n");
    LeaveCriticalSection(&gPrint);
    InterlockedIncrement(&gRecords);
}

static BOOL WINAPI OnCtrl(DWORD type)
{
    (void)type;
    gStop = 1;
    return TRUE;
}

static DWORD WINAPI ReaderLoop(LPVOID unused)
{
    MESSAGE* message = (MESSAGE*)malloc(sizeof(MESSAGE));
    (void)unused;
    if (message == NULL) return 1;

    /* The driver sends without waiting, so several reads must always be outstanding (READER_THREADS of them): that is what
     * keeps records from being dropped. */
    while (!gStop) {
        HRESULT hr = FilterGetMessage(gPort, &message->Header, sizeof(MESSAGE), NULL);
        if (FAILED(hr)) {
            break;
        }
        /* The body length is not reported separately; records carry their own lengths, so give the decoder the full buffer. */
        PrintMessage(message->Body, MSG_BODY_BYTES);
    }
    free(message);
    return 0;
}

static int CmdWatch(int argc, char** argv)
{
    static WCHAR folders[16][1024];
    WCHAR* list[16];
    SCOPE_MSG* msg = (SCOPE_MSG*)calloc(1, sizeof(SCOPE_MSG));
    AUDITOR_REPLY reply;
    UINT32 mask;
    int recursive, seconds, count, i;
    HANDLE threads[READER_THREADS];
    HRESULT hr;
    DWORD started;

    if (msg == NULL) return 2;
    count = ParseScopeArgs(argc, argv, 2, &mask, &recursive, &seconds, folders, list);
    if (count <= 0) {
        printf("watch needs at least one folder\n");
        free(msg);
        return 2;
    }
    InitializeCriticalSection(&gPrint);
    SetConsoleCtrlHandler(OnCtrl, TRUE);

    if (!Connect()) { free(msg); return 1; }

    for (i = 0; i < READER_THREADS; i++) {
        threads[i] = CreateThread(NULL, 0, ReaderLoop, NULL, 0, NULL);
    }

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, mask, recursive, list, count);
    hr = Send(msg->Bytes, msg->Length, &reply);
    PrintReply("scope", hr, &reply);
    if (FAILED(hr)) { gStop = 1; }
    else printf("watching %d folder(s) for %d s; Ctrl+C to stop\n", count, seconds);

    started = GetTickCount();
    while (!gStop && (GetTickCount() - started) < (DWORD)seconds * 1000u) {
        Sleep(200);
    }
    gStop = 1;

    BuildScope(msg, AUDITOR_CMD_SET_SCOPE, 0, 1, NULL, 0);                       /* leave auditing off */
    Send(msg->Bytes, msg->Length, &reply);
    PrintReply("cleared", S_OK, &reply);
    printf("%ld record(s), %ld notice(s) printed\n", (long)gRecords, (long)gNotices);

    CloseHandle(gPort);                                                          /* unblocks the readers */
    WaitForMultipleObjects(READER_THREADS, threads, TRUE, 3000);
    free(msg);
    return FAILED(hr) ? 1 : 0;
}

int main(int argc, char** argv)
{
    if (argc < 2) {
        printf("usage: AuditorPortTool selftest | set | clear | stats | watch  (see the comment at the top of AuditorPortTool.c)\n");
        return 2;
    }
    if (strcmp(argv[1], "selftest") == 0) return CmdSelfTest();
    if (strcmp(argv[1], "watch") == 0)    return CmdWatch(argc, argv);
    if (strcmp(argv[1], "set") == 0 || strcmp(argv[1], "clear") == 0 || strcmp(argv[1], "stats") == 0) {
        return CmdSetClearStats(argv[1], argc, argv);
    }
    printf("unknown command '%s'\n", argv[1]);
    return 2;
}
