/* emit.c — D5: build records with the portable record.c, hand them to the agent over the port, and keep the counters.
 * Nothing here ever waits: a record the agent cannot take right now is counted as dropped and an overflow notice is
 * armed, to be sent with the next record that does get through. */
#include "driver.h"
#include "record.h"
#include "driver_protocol.h"

static volatile LONG64 gSentTotal;
static volatile LONG64 gDroppedTotal;
static volatile LONG   gOverflowPending;

VOID EmitInit(VOID)
{
    gSentTotal = 0;
    gDroppedTotal = 0;
    gOverflowPending = 0;
}

UINT64 EmitSentTotal(VOID)
{
    return (UINT64)InterlockedCompareExchange64(&gSentTotal, 0, 0);
}

UINT64 EmitDroppedTotal(VOID)
{
    return (UINT64)InterlockedCompareExchange64(&gDroppedTotal, 0, 0);
}

VOID EmitCountDrop(VOID)
{
    InterlockedIncrement64(&gDroppedTotal);
    InterlockedExchange(&gOverflowPending, 1);
}

/* A new agent should hear about drops that happened while nobody was connected. */
VOID EmitNoteReconnect(VOID)
{
    if (EmitDroppedTotal() != 0) {
        InterlockedExchange(&gOverflowPending, 1);
    }
}

static VOID SendOverflowNotice(VOID)
{
    AUDITOR_OVERFLOW_NOTICE notice;

    RtlZeroMemory(&notice, sizeof(notice));
    notice.marker = AUDITOR_NOTICE_MARKER;
    notice.kind = AUDITOR_NOTICE_OVERFLOW;
    notice.protocolVersion = AUDITOR_PROTOCOL_VERSION;
    notice.droppedTotal = EmitDroppedTotal();

    if (!NT_SUCCESS(CommsSend(&notice, (ULONG)sizeof(notice)))) {
        InterlockedExchange(&gOverflowPending, 1);      /* try again with the next record */
    }
}

VOID EmitRecord(_In_reads_bytes_(bytes) const UCHAR* record, ULONG bytes)
{
    if (NT_SUCCESS(CommsSend((PVOID)record, bytes))) {
        InterlockedIncrement64(&gSentTotal);
        if (InterlockedCompareExchange(&gOverflowPending, 0, 1) == 1) {
            SendOverflowNotice();
        }
    } else {
        EmitCountDrop();
    }
}

PUCHAR EmitBuild(
    UINT8 operation, UINT8 objectType, ULONG processId, LONG status,
    _In_reads_(sidChars) const WCHAR* sid, SIZE_T sidChars,
    _In_reads_(pathChars) const WCHAR* path, SIZE_T pathChars,
    _In_reads_opt_(newChars) const WCHAR* newPath, SIZE_T newChars,
    _Out_ ULONG* bytes)
{
    LARGE_INTEGER now;
    SIZE_T size = AuditRecordSize(sidChars, pathChars, newChars);
    SIZE_T written;
    PUCHAR buffer;

    *bytes = 0;

    if (size == 0 || size > AUDITOR_MAX_RECORD_BYTES) {
        EmitCountDrop();
        return NULL;
    }

    buffer = (PUCHAR)AudAlloc(size);
    if (buffer == NULL) {
        EmitCountDrop();
        return NULL;
    }

    KeQuerySystemTimePrecise(&now);

    written = AuditBuildRecord(
        buffer, size, operation, objectType, 0, processId, status, (uint64_t)now.QuadPart,
        sid, sidChars, path, pathChars, newPath, newChars);
    if (written == 0) {
        ExFreePoolWithTag(buffer, AUDITOR_POOL_TAG);
        EmitCountDrop();
        return NULL;
    }

    *bytes = (ULONG)written;
    return buffer;
}

VOID EmitFree(_In_ PUCHAR record)
{
    ExFreePoolWithTag(record, AUDITOR_POOL_TAG);
}

VOID EmitNow(
    UINT8 operation, UINT8 objectType, ULONG processId, LONG status,
    _In_reads_(sidChars) const WCHAR* sid, SIZE_T sidChars,
    _In_reads_(pathChars) const WCHAR* path, SIZE_T pathChars)
{
    ULONG bytes;
    PUCHAR record = EmitBuild(operation, objectType, processId, status, sid, sidChars, path, pathChars, NULL, 0, &bytes);

    if (record != NULL) {
        EmitRecord(record, bytes);
        EmitFree(record);
    }
}

VOID EmitGetSid(_In_opt_ PSECURITY_SUBJECT_CONTEXT subject, _Out_writes_(AUDITOR_SID_CHARS) WCHAR* sid, _Out_ USHORT* sidChars)
{
    SECURITY_SUBJECT_CONTEXT captured;
    PSECURITY_SUBJECT_CONTEXT context = subject;
    PACCESS_TOKEN token;
    PTOKEN_USER user = NULL;

    *sidChars = 0;

    if (context == NULL) {
        SeCaptureSubjectContext(&captured);
        context = &captured;
    }

    token = SeQuerySubjectContextToken(context);
    if (token != NULL && NT_SUCCESS(SeQueryInformationToken(token, TokenUser, (PVOID*)&user)) && user != NULL) {
        UNICODE_STRING text;

        text.Buffer = sid;
        text.Length = 0;
        text.MaximumLength = (USHORT)(AUDITOR_SID_CHARS * sizeof(WCHAR));

        /* Written into our own buffer (no allocation); a SID too long for 68 characters fails and is sent as empty. */
        if (NT_SUCCESS(RtlConvertSidToUnicodeString(&text, user->User.Sid, FALSE))) {
            *sidChars = (USHORT)(text.Length / sizeof(WCHAR));
        }
#pragma warning(suppress: 4996)
        ExFreePool(user);
    }

    if (subject == NULL) {
        SeReleaseSubjectContext(&captured);
    }
}
