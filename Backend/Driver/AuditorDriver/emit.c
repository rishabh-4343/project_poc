/* emit.c — D5: build records with the portable record.c, hand them to the agent over the port, and keep the counters.
 *
 * The file-operation threads never wait: EmitRecord copies the finished record into a small bounded queue and returns.
 * One system thread drains the queue and sends with a finite FltSendMessage timeout, so a record that arrives while the
 * agent is between two reads (it may keep only one read outstanding) is retried for a moment instead of being lost.
 * A record is counted as dropped only when the queue is full, no agent is connected, or the agent stays unresponsive; an
 * overflow notice is then armed, to be sent with the next record that does get through. */
#include "driver.h"
#include "record.h"
#include "driver_protocol.h"

#define EMIT_QUEUE_MAX_COUNT    4096                    /* queued records */
#define EMIT_QUEUE_MAX_BYTES    (16 * 1024 * 1024)      /* queued record bytes */
#define EMIT_SEND_TRIES         5                       /* attempts per record, each waiting up to EMIT_SEND_WAIT_MS */
#define EMIT_SEND_WAIT_MS       200

typedef struct _EMIT_NODE {
    LIST_ENTRY  Link;
    ULONG       Bytes;
    UCHAR       Data[1];
} EMIT_NODE;

static volatile LONG64 gSentTotal;
static volatile LONG64 gDroppedTotal;
static volatile LONG   gOverflowPending;

static KSPIN_LOCK      gQueueLock;
static LIST_ENTRY      gQueue;
static ULONG           gQueueCount;
static SIZE_T          gQueueBytes;
static BOOLEAN         gAccepting;              /* guarded by gQueueLock */
static KEVENT          gQueueEvent;             /* synchronization event: a record was queued, or stop was requested */
static volatile LONG   gStop;
static PETHREAD        gWorker;

VOID EmitInit(VOID)
{
    gSentTotal = 0;
    gDroppedTotal = 0;
    gOverflowPending = 0;

    KeInitializeSpinLock(&gQueueLock);
    InitializeListHead(&gQueue);
    gQueueCount = 0;
    gQueueBytes = 0;
    gAccepting = FALSE;
    gStop = 0;
    gWorker = NULL;
    KeInitializeEvent(&gQueueEvent, SynchronizationEvent, FALSE);
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

/* Worker thread only. Waits a bounded time for the agent to post a read; gives up on a disconnected port at once. */
static NTSTATUS SendWithRetry(_In_ PVOID message, ULONG bytes)
{
    NTSTATUS status = STATUS_IO_TIMEOUT;
    ULONG attempt;

    for (attempt = 0; attempt < EMIT_SEND_TRIES && !gStop; attempt++) {
        status = CommsSend(message, bytes, EMIT_SEND_WAIT_MS);
        if (status != STATUS_IO_TIMEOUT) {
            break;
        }
    }
    return status;
}

static VOID SendOverflowNotice(VOID)
{
    AUDITOR_OVERFLOW_NOTICE notice;

    RtlZeroMemory(&notice, sizeof(notice));
    notice.marker = AUDITOR_NOTICE_MARKER;
    notice.kind = AUDITOR_NOTICE_OVERFLOW;
    notice.protocolVersion = AUDITOR_PROTOCOL_VERSION;
    notice.droppedTotal = EmitDroppedTotal();

    if (!NT_SUCCESS(SendWithRetry(&notice, (ULONG)sizeof(notice)))) {
        InterlockedExchange(&gOverflowPending, 1);      /* try again with the next record */
    }
}

static EMIT_NODE* QueuePop(VOID)
{
    EMIT_NODE* node = NULL;
    KIRQL irql;

    KeAcquireSpinLock(&gQueueLock, &irql);
    if (!IsListEmpty(&gQueue)) {
        node = CONTAINING_RECORD(RemoveHeadList(&gQueue), EMIT_NODE, Link);
        gQueueCount--;
        gQueueBytes -= node->Bytes;
    }
    KeReleaseSpinLock(&gQueueLock, irql);
    return node;
}

static VOID EmitWorker(_In_ PVOID context)
{
    UNREFERENCED_PARAMETER(context);

    for (;;) {
        EMIT_NODE* node;

        while (!gStop && (node = QueuePop()) != NULL) {
            if (NT_SUCCESS(SendWithRetry(node->Data, node->Bytes))) {
                InterlockedIncrement64(&gSentTotal);
                if (InterlockedCompareExchange(&gOverflowPending, 0, 1) == 1) {
                    SendOverflowNotice();
                }
            } else {
                EmitCountDrop();
            }
            ExFreePoolWithTag(node, AUDITOR_POOL_TAG);
        }
        if (gStop) {
            break;
        }
        KeWaitForSingleObject(&gQueueEvent, Executive, KernelMode, FALSE, NULL);
    }
    PsTerminateSystemThread(STATUS_SUCCESS);
}

NTSTATUS EmitStart(VOID)
{
    HANDLE thread = NULL;
    NTSTATUS status;
    KIRQL irql;

    status = PsCreateSystemThread(&thread, THREAD_ALL_ACCESS, NULL, NULL, NULL, EmitWorker, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    status = ObReferenceObjectByHandle(thread, SYNCHRONIZE, *PsThreadType, KernelMode, (PVOID*)&gWorker, NULL);
    ZwClose(thread);
    if (!NT_SUCCESS(status)) {
        gStop = 1;                                      /* the worker cannot be waited for; let it exit on its own */
        KeSetEvent(&gQueueEvent, IO_NO_INCREMENT, FALSE);
        gWorker = NULL;
        return status;
    }

    KeAcquireSpinLock(&gQueueLock, &irql);
    gAccepting = TRUE;
    KeReleaseSpinLock(&gQueueLock, irql);
    return STATUS_SUCCESS;
}

/* Stops accepting records, waits for the worker to exit and frees whatever was still queued. Callbacks that are still
 * running afterwards (until FltUnregisterFilter returns) find the queue closed and just count a drop. */
VOID EmitShutdown(VOID)
{
    KIRQL irql;
    EMIT_NODE* node;

    KeAcquireSpinLock(&gQueueLock, &irql);
    gAccepting = FALSE;
    KeReleaseSpinLock(&gQueueLock, irql);

    gStop = 1;
    KeSetEvent(&gQueueEvent, IO_NO_INCREMENT, FALSE);
    if (gWorker != NULL) {
        KeWaitForSingleObject(gWorker, Executive, KernelMode, FALSE, NULL);
        ObDereferenceObject(gWorker);
        gWorker = NULL;
    }

    while ((node = QueuePop()) != NULL) {
        ExFreePoolWithTag(node, AUDITOR_POOL_TAG);
    }
}

/* Never waits and is safe at any IRQL up to DISPATCH_LEVEL: it only copies the record and queues it. */
VOID EmitRecord(_In_reads_bytes_(bytes) const UCHAR* record, ULONG bytes)
{
    EMIT_NODE* node = NULL;
    BOOLEAN queued = FALSE;
    KIRQL irql;

    if (bytes != 0 && bytes <= AUDITOR_MAX_RECORD_BYTES) {
        node = (EMIT_NODE*)AudAlloc(FIELD_OFFSET(EMIT_NODE, Data) + bytes);
    }
    if (node != NULL) {
        RtlCopyMemory(node->Data, record, bytes);
        node->Bytes = bytes;

        KeAcquireSpinLock(&gQueueLock, &irql);
        if (gAccepting && gQueueCount < EMIT_QUEUE_MAX_COUNT && gQueueBytes + bytes <= EMIT_QUEUE_MAX_BYTES) {
            InsertTailList(&gQueue, &node->Link);
            gQueueCount++;
            gQueueBytes += bytes;
            queued = TRUE;
        }
        KeReleaseSpinLock(&gQueueLock, irql);
    }

    if (queued) {
        KeSetEvent(&gQueueEvent, IO_NO_INCREMENT, FALSE);
    } else {
        if (node != NULL) {
            ExFreePoolWithTag(node, AUDITOR_POOL_TAG);
        }
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
