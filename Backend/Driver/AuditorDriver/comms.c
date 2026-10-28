/* comms.c — D2: the communication port. One agent connects, sends the watch scope (SET_SCOPE) and reads the records
 * the driver sends with FltSendMessage. Messages follow driver_protocol.h. */
#include "driver.h"
#include "scope.h"
#include "driver_protocol.h"

static PFLT_PORT    gServerPort = NULL;
static PFLT_PORT    gClientPort = NULL;
static EX_PUSH_LOCK gPortLock;

static NTSTATUS PortConnect(
    _In_ PFLT_PORT ClientPort, _In_opt_ PVOID ServerPortCookie, _In_reads_bytes_opt_(SizeOfContext) PVOID ConnectionContext,
    _In_ ULONG SizeOfContext, _Outptr_result_maybenull_ PVOID* ConnectionPortCookie)
{
    UNREFERENCED_PARAMETER(ServerPortCookie);
    UNREFERENCED_PARAMETER(ConnectionContext);
    UNREFERENCED_PARAMETER(SizeOfContext);

    *ConnectionPortCookie = NULL;

    FltAcquirePushLockExclusive(&gPortLock);
    if (gClientPort != NULL) {
        FltReleasePushLock(&gPortLock);
        return STATUS_ALREADY_REGISTERED;       /* the port also allows one connection; this is the backstop */
    }
    gClientPort = ClientPort;
    FltReleasePushLock(&gPortLock);

    EmitNoteReconnect();
    DbgPrint("wfsa: agent connected\n");
    return STATUS_SUCCESS;
}

static VOID PortDisconnect(_In_opt_ PVOID ConnectionCookie)
{
    PFLT_PORT port;

    UNREFERENCED_PARAMETER(ConnectionCookie);

    FltAcquirePushLockExclusive(&gPortLock);
    port = gClientPort;
    gClientPort = NULL;                         /* senders see NULL from now on, so closing is safe */
    FltReleasePushLock(&gPortLock);

    if (port != NULL) {
        FltCloseClientPort(gFilter, &port);
    }
    DbgPrint("wfsa: agent disconnected (scope kept; records are counted as dropped until it returns)\n");
}

static VOID LogScope(UINT32 mask, UINT32 count, const UCHAR* folders)
{
    const UCHAR* p = folders;
    UINT32 i;

    DbgPrint("wfsa: scope stored: %lu folder(s), operation mask 0x%08lx\n", count, mask);
    for (i = 0; i < count && i < 32; i++) {
        UINT16 flags = (UINT16)(p[0] | (p[1] << 8));
        UINT16 bytes = (UINT16)(p[2] | (p[3] << 8));
        UNICODE_STRING path;

        path.Buffer = (PWCH)(p + 4);
        path.Length = bytes;
        path.MaximumLength = bytes;
        DbgPrint("wfsa:   %s %wZ\n", (flags & AUDITOR_FOLDER_RECURSIVE) ? "[recursive]" : "[direct]   ", &path);
        p += 4u + bytes;
    }
    if (count > 32) {
        DbgPrint("wfsa:   ... and %lu more\n", count - 32);
    }
}

static NTSTATUS ScopeStatusToNtStatus(SCOPE_STATUS s)
{
    switch (s) {
    case SCOPE_OK:              return STATUS_SUCCESS;
    case SCOPE_ERR_TOO_SHORT:
    case SCOPE_ERR_TOO_BIG:     return STATUS_INVALID_BUFFER_SIZE;
    case SCOPE_ERR_VERSION:     return STATUS_REVISION_MISMATCH;
    default:                    return STATUS_INVALID_PARAMETER;
    }
}

/* Validates the copy of the message and, when it is good, hands the copy itself to the scope state (which then owns it). */
static NTSTATUS ApplyScope(_In_ PUCHAR copy, ULONG length, _Out_ BOOLEAN* owned)
{
    UINT32 mask = 0;
    UINT32 count = 0;
    const UCHAR* folders = NULL;
    SIZE_T folderBytes = 0;
    SCOPE_STATUS result;

    *owned = FALSE;

    result = ScopeValidate(copy + sizeof(AUDITOR_COMMAND_HEADER), length - sizeof(AUDITOR_COMMAND_HEADER), &mask, &count, &folders, &folderBytes);
    if (result != SCOPE_OK) {
        DbgPrint("wfsa: scope rejected (%d)\n", (int)result);
        return ScopeStatusToNtStatus(result);
    }

    if (count == 0) {
        ScopeStateSet(NULL, NULL, 0, 0);        /* an empty scope stops all auditing */
        DbgPrint("wfsa: scope cleared\n");
        return STATUS_SUCCESS;
    }

    ScopeStateSet(copy, folders, count, mask);
    *owned = TRUE;
    LogScope(mask, count, folders);
    return STATUS_SUCCESS;
}

static NTSTATUS PortMessage(
    _In_opt_ PVOID PortCookie,
    _In_reads_bytes_opt_(InputBufferLength) PVOID InputBuffer, _In_ ULONG InputBufferLength,
    _Out_writes_bytes_to_opt_(OutputBufferLength, *ReturnOutputBufferLength) PVOID OutputBuffer, _In_ ULONG OutputBufferLength,
    _Out_ PULONG ReturnOutputBufferLength)
{
    PUCHAR copy;
    BOOLEAN owned = FALSE;
    NTSTATUS status;
    const AUDITOR_COMMAND_HEADER* header;
    AUDITOR_REPLY reply;

    UNREFERENCED_PARAMETER(PortCookie);

    *ReturnOutputBufferLength = 0;

    if (InputBuffer == NULL || InputBufferLength < sizeof(AUDITOR_COMMAND_HEADER)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (InputBufferLength > AUDITOR_MAX_SCOPE_BYTES + sizeof(AUDITOR_COMMAND_HEADER)) {
        return STATUS_INVALID_BUFFER_SIZE;
    }
    if (OutputBuffer == NULL || OutputBufferLength < sizeof(AUDITOR_REPLY)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    /* The caller's memory can change under us, so work on a kernel copy and validate that. */
    copy = (PUCHAR)AudAlloc(InputBufferLength);
    if (copy == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    __try {
        if (ExGetPreviousMode() == UserMode) {
            ProbeForRead(InputBuffer, InputBufferLength, sizeof(UCHAR));
            ProbeForWrite(OutputBuffer, OutputBufferLength, sizeof(UCHAR));
        }
        RtlCopyMemory(copy, InputBuffer, InputBufferLength);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ExFreePoolWithTag(copy, AUDITOR_POOL_TAG);
        return GetExceptionCode();
    }

    header = (const AUDITOR_COMMAND_HEADER*)copy;

    if (header->protocolVersion != AUDITOR_PROTOCOL_VERSION) {
        status = STATUS_REVISION_MISMATCH;
    } else if (header->command == AUDITOR_CMD_SET_SCOPE) {
        status = ApplyScope(copy, InputBufferLength, &owned);
    } else if (header->command == AUDITOR_CMD_GET_STATS) {
        status = STATUS_SUCCESS;
    } else {
        status = STATUS_INVALID_DEVICE_REQUEST;
    }

    if (!owned) {
        ExFreePoolWithTag(copy, AUDITOR_POOL_TAG);
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlZeroMemory(&reply, sizeof(reply));
    reply.protocolVersion = AUDITOR_PROTOCOL_VERSION;
    ScopeStateGet(&reply.operationMask, &reply.folderCount);
    reply.droppedTotal = EmitDroppedTotal();
    reply.sentTotal = EmitSentTotal();

    __try {
        RtlCopyMemory(OutputBuffer, &reply, sizeof(reply));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return GetExceptionCode();
    }

    *ReturnOutputBufferLength = (ULONG)sizeof(reply);
    return STATUS_SUCCESS;
}

NTSTATUS CommsStart(VOID)
{
    PSECURITY_DESCRIPTOR descriptor = NULL;
    OBJECT_ATTRIBUTES attributes;
    UNICODE_STRING name;
    NTSTATUS status;

    FltInitializePushLock(&gPortLock);

    /* The default descriptor gives access to administrators and SYSTEM only, which is what a service agent runs as. */
    status = FltBuildDefaultSecurityDescriptor(&descriptor, FLT_PORT_ALL_ACCESS);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&name, AUDITOR_PORT_NAME);
    InitializeObjectAttributes(&attributes, &name, OBJ_KERNEL_HANDLE | OBJ_CASE_INSENSITIVE, NULL, descriptor);

    status = FltCreateCommunicationPort(gFilter, &gServerPort, &attributes, NULL, PortConnect, PortDisconnect, PortMessage, 1);
    FltFreeSecurityDescriptor(descriptor);

    if (!NT_SUCCESS(status)) {
        gServerPort = NULL;
    }
    return status;
}

VOID CommsStop(VOID)
{
    if (gServerPort != NULL) {
        FltCloseCommunicationPort(gServerPort);
        gServerPort = NULL;
    }
}

NTSTATUS CommsSend(_In_reads_bytes_(bytes) PVOID message, ULONG bytes)
{
    LARGE_INTEGER timeout;
    NTSTATUS status;

    /* A zero timeout means the send never waits for the agent: it succeeds only when an agent thread is already waiting in
     * FilterGetMessage, otherwise the record is dropped and counted. The agent must keep several such reads outstanding. */
    timeout.QuadPart = 0;

    FltAcquirePushLockShared(&gPortLock);
    if (gClientPort == NULL) {
        status = STATUS_PORT_DISCONNECTED;
    } else {
        status = FltSendMessage(gFilter, &gClientPort, message, bytes, NULL, NULL, &timeout);
    }
    FltReleasePushLock(&gPortLock);

    /* STATUS_TIMEOUT counts as success for NT_SUCCESS, but it means nothing was delivered. */
    if (status == STATUS_TIMEOUT) {
        return STATUS_IO_TIMEOUT;
    }
    return status;
}
