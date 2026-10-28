/* driver.c — DriverEntry, registration, load and unload. Operation callbacks live in capture.c, the port in comms.c. */
#include "driver.h"

PFLT_FILTER gFilter = NULL;

DRIVER_INITIALIZE DriverEntry;

NTSTATUS FilterUnload(_In_ FLT_FILTER_UNLOAD_FLAGS Flags);

NTSTATUS InstanceSetup(
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_SETUP_FLAGS Flags,
    _In_ DEVICE_TYPE VolumeDeviceType,
    _In_ FLT_FILESYSTEM_TYPE VolumeFilesystemType);

NTSTATUS InstanceQueryTeardown(
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_QUERY_TEARDOWN_FLAGS Flags);

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, FilterUnload)
#pragma alloc_text(PAGE, InstanceSetup)
#pragma alloc_text(PAGE, InstanceQueryTeardown)
#endif

static CONST FLT_REGISTRATION gRegistration = {
    sizeof(FLT_REGISTRATION),           /* Size */
    FLT_REGISTRATION_VERSION,           /* Version */
    0,                                  /* Flags */
    gContextRegistration,               /* ContextRegistration */
    gOperationCallbacks,                /* OperationRegistration */
    FilterUnload,                       /* FilterUnloadCallback */
    InstanceSetup,                      /* InstanceSetupCallback */
    InstanceQueryTeardown,              /* InstanceQueryTeardownCallback */
    NULL,                               /* InstanceTeardownStartCallback */
    NULL,                               /* InstanceTeardownCompleteCallback */
    NULL,                               /* GenerateFileNameCallback */
    NULL,                               /* NormalizeNameComponentCallback */
    NULL,                               /* NormalizeContextCleanupCallback */
    NULL,                               /* TransactionNotificationCallback */
    NULL,                               /* NormalizeNameComponentExCallback */
    NULL                                /* SectionNotificationCallback */
};

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    NTSTATUS status;

    UNREFERENCED_PARAMETER(RegistryPath);

    /* Non-paged allocations default to no-execute memory (needed on Windows versions before 2004). */
    ExInitializeDriverRuntime(DrvRtPoolNxOptIn);

    ScopeStateInit();
    EmitInit();

    status = FltRegisterFilter(DriverObject, &gRegistration, &gFilter);
    if (!NT_SUCCESS(status)) {
        ScopeStateCleanup();
        return status;
    }

    status = CommsStart();
    if (!NT_SUCCESS(status)) {
        FltUnregisterFilter(gFilter);
        gFilter = NULL;
        ScopeStateCleanup();
        return status;
    }

    /* Filtering starts last: the scope is empty until the agent sends one, so nothing is captured before then. */
    status = FltStartFiltering(gFilter);
    if (!NT_SUCCESS(status)) {
        CommsStop();
        FltUnregisterFilter(gFilter);
        gFilter = NULL;
        ScopeStateCleanup();
    }

    return status;
}

NTSTATUS FilterUnload(_In_ FLT_FILTER_UNLOAD_FLAGS Flags)
{
    UNREFERENCED_PARAMETER(Flags);
    PAGED_CODE();

    /* Close the port first so no new message arrives, then unregister: FltUnregisterFilter waits for callbacks that are
     * still running, and only after it returns is it safe to free what they use. A mandatory unload (shutdown) takes the
     * same path; nothing needs draining because every record is sent as it is built. */
    CommsStop();
    FltUnregisterFilter(gFilter);
    gFilter = NULL;
    ScopeStateCleanup();
    return STATUS_SUCCESS;
}

/* Attach to local volumes only. Network redirector volumes are not ours (a file server is audited where the
 * disks are), and RAW volumes have no file system to watch. */
NTSTATUS InstanceSetup(
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_SETUP_FLAGS Flags,
    _In_ DEVICE_TYPE VolumeDeviceType,
    _In_ FLT_FILESYSTEM_TYPE VolumeFilesystemType)
{
    UNREFERENCED_PARAMETER(FltObjects);
    UNREFERENCED_PARAMETER(Flags);
    PAGED_CODE();

    if (VolumeDeviceType == FILE_DEVICE_NETWORK_FILE_SYSTEM || VolumeFilesystemType == FLT_FSTYPE_RAW) {
        return STATUS_FLT_DO_NOT_ATTACH;
    }
    return STATUS_SUCCESS;
}

/* Allow an instance to be detached manually (fltmc detach). */
NTSTATUS InstanceQueryTeardown(
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_ FLT_INSTANCE_QUERY_TEARDOWN_FLAGS Flags)
{
    UNREFERENCED_PARAMETER(FltObjects);
    UNREFERENCED_PARAMETER(Flags);
    PAGED_CODE();

    return STATUS_SUCCESS;
}
