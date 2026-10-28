/* capture.c — D3/D4: the operation callbacks. Each one does the least it can: reject by scope and operation mask first
 * (so unwatched files cost almost nothing), then resolve the NT path, capture the actor, and hand one record to emit.c.
 *
 *   Create        PostCreate           Created, Open, Modified (overwrite), and the denied variants; arms delete-on-close.
 *                                      An Open with read access becomes Content View when reads are in scope (agent merges the volume).
 *   SetInfo       PreSetInfo/Post      Renamed/Moved (old and new path, built before the rename, status patched after),
 *                                      delete requests (a flag on the handle, confirmed at cleanup)
 *   SetSecurity   PreSetSecurity/Post  OwnerChange, PermissionChange, AuditingChange (one record per part of the descriptor)
 *   Write         PreWrite             Modified, once per open handle (the agent merges the rest)
 *   Cleanup       PreCleanup           Deleted, when this handle asked for a delete that is still pending
 *
 * An operation that failed for a reason other than access denied (name collision, not found, ...) changed nothing and
 * is not reported. */
#include "driver.h"
#include "scope.h"
#include "record.h"

/* ---- per-handle state ------------------------------------------------------------------------------------------- */
typedef struct _HANDLE_CTX {
    volatile LONG   ModifiedReported;   /* a Modified record was already sent for this handle */
    volatile LONG   DeleteRequested;    /* this handle asked for the file to be deleted */
    ULONG           DeletePid;          /* who asked, captured at that moment (the closing process may differ) */
    USHORT          DeleteSidChars;
    WCHAR           DeleteSid[AUDITOR_SID_CHARS];
} HANDLE_CTX;

CONST FLT_CONTEXT_REGISTRATION gContextRegistration[] = {
    { FLT_STREAMHANDLE_CONTEXT, 0, NULL, sizeof(HANDLE_CTX), AUDITOR_POOL_TAG },
    { FLT_CONTEXT_END }
};

/* Returns the handle's context with a reference the caller must release, or NULL (not found, or contexts unsupported). */
static HANDLE_CTX* GetHandleCtx(_In_ PCFLT_RELATED_OBJECTS objects, BOOLEAN create)
{
    HANDLE_CTX* ctx = NULL;
    PFLT_CONTEXT existing = NULL;
    NTSTATUS status;

    if (objects->FileObject == NULL) {
        return NULL;
    }
    if (NT_SUCCESS(FltGetStreamHandleContext(objects->Instance, objects->FileObject, (PFLT_CONTEXT*)&ctx))) {
        return ctx;
    }
    if (!create) {
        return NULL;
    }
    if (!NT_SUCCESS(FltAllocateContext(gFilter, FLT_STREAMHANDLE_CONTEXT, sizeof(HANDLE_CTX), NonPagedPoolNx, (PFLT_CONTEXT*)&ctx))) {
        return NULL;
    }
    RtlZeroMemory(ctx, sizeof(*ctx));

    status = FltSetStreamHandleContext(objects->Instance, objects->FileObject, FLT_SET_CONTEXT_KEEP_IF_EXISTS, ctx, &existing);
    if (NT_SUCCESS(status)) {
        return ctx;                                     /* the caller owns our allocation reference */
    }
    FltReleaseContext(ctx);
    if (status == STATUS_FLT_CONTEXT_ALREADY_DEFINED) {
        return (HANDLE_CTX*)existing;                   /* a racing callback created it; its reference is ours now */
    }
    return NULL;
}

/* ---- completion state carried from a pre-callback to its post-callback ---------------------------------------------- */
#define POST_KIND_RECORDS       1       /* finished records waiting for their status */
#define POST_KIND_DISPOSITION   2       /* a delete request waiting to see whether it succeeded */

typedef struct _POST_CTX {
    ULONG   Kind;
    ULONG   Count;                      /* RECORDS */
    PUCHAR  Record[3];
    ULONG   Bytes[3];
    ULONG   Pid;                        /* DISPOSITION */
    USHORT  SidChars;
    WCHAR   Sid[AUDITOR_SID_CHARS];
} POST_CTX;

static POST_CTX* AllocPostCtx(ULONG kind)
{
    POST_CTX* ctx = (POST_CTX*)AudAlloc(sizeof(POST_CTX));
    if (ctx != NULL) {
        ctx->Kind = kind;
    }
    return ctx;
}

static VOID FreePostCtx(_In_ POST_CTX* ctx)
{
    ULONG i;
    for (i = 0; i < ctx->Count; i++) {
        if (ctx->Record[i] != NULL) {
            EmitFree(ctx->Record[i]);
        }
    }
    ExFreePoolWithTag(ctx, AUDITOR_POOL_TAG);
}

/* ---- helpers ------------------------------------------------------------------------------------------------------ */

/* The NT path of the file the operation targets. Named streams are skipped (STATUS_NOT_SUPPORTED): their opens are
 * noise, and the file itself is reported when it is opened. On success the caller releases *info. */
static NTSTATUS GetAuditName(_In_ PFLT_CALLBACK_DATA data, _Outptr_ PFLT_FILE_NAME_INFORMATION* info)
{
    NTSTATUS status;

    *info = NULL;

    status = FltGetFileNameInformation(data, FLT_FILE_NAME_NORMALIZED | FLT_FILE_NAME_QUERY_DEFAULT, info);
    if (!NT_SUCCESS(status)) {
        /* A failed create has no normalized name; the name it was opened with still identifies the target. */
        status = FltGetFileNameInformation(data, FLT_FILE_NAME_OPENED | FLT_FILE_NAME_QUERY_DEFAULT, info);
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = FltParseFileNameInformation(*info);
    if (!NT_SUCCESS(status) || (*info)->Stream.Length != 0 || (*info)->Name.Length == 0 || ((*info)->Name.Length / sizeof(WCHAR)) > 0x7FFF) {
        FltReleaseFileNameInformation(*info);
        *info = NULL;
        return STATUS_NOT_SUPPORTED;
    }
    return STATUS_SUCCESS;
}

static BOOLEAN InScope(_In_ const UNICODE_STRING* name)
{
    return ScopeStateMatch(name->Buffer, name->Length / sizeof(WCHAR));
}

static UINT8 ObjectTypeOf(_In_ PCFLT_RELATED_OBJECTS objects, BOOLEAN fallbackIsFolder)
{
    BOOLEAN isDirectory = FALSE;

    if (objects->FileObject != NULL && NT_SUCCESS(FltIsDirectory(objects->FileObject, objects->Instance, &isDirectory))) {
        return isDirectory ? (UINT8)OBJ_FOLDER : (UINT8)OBJ_FILE;
    }
    return fallbackIsFolder ? (UINT8)OBJ_FOLDER : (UINT8)OBJ_FILE;
}

/* Read-data access on this open (for a folder, list-directory: they share the FILE_READ_DATA bit). The granted access
 * is used on a successful open, the requested access on a denied one. */
static BOOLEAN ReadDataAccess(_In_ PFLT_CALLBACK_DATA data, BOOLEAN denied)
{
    PIO_SECURITY_CONTEXT sec = data->Iopb->Parameters.Create.SecurityContext;
    ACCESS_MASK access = 0;

    if (sec == NULL) {
        return FALSE;
    }
    if (!denied && sec->AccessState != NULL) {
        access = sec->AccessState->PreviouslyGrantedAccess;   /* generic rights already mapped to specific ones */
    }
    if (access == 0) {
        access = sec->DesiredAccess;
    }
    return (access & FILE_READ_DATA) != 0;
}

/* The last path component is dropped on both names; equal parents mean a rename, different parents a move. */
static BOOLEAN SameParent(_In_ const UNICODE_STRING* a, _In_ const UNICODE_STRING* b)
{
    UNICODE_STRING parentA = *a;
    UNICODE_STRING parentB = *b;

    while (parentA.Length >= sizeof(WCHAR) && parentA.Buffer[parentA.Length / sizeof(WCHAR) - 1] != L'\\') {
        parentA.Length -= sizeof(WCHAR);
    }
    while (parentB.Length >= sizeof(WCHAR) && parentB.Buffer[parentB.Length / sizeof(WCHAR) - 1] != L'\\') {
        parentB.Length -= sizeof(WCHAR);
    }
    return RtlEqualUnicodeString(&parentA, &parentB, TRUE);
}

/* Post-callbacks may run at DISPATCH_LEVEL, where names cannot be queried and FltSendMessage cannot be called.
 * Run the work now when the IRQL allows, otherwise let the filter manager call it from a safe context. */
static FLT_POSTOP_CALLBACK_STATUS RunWhenSafe(
    _Inout_ PFLT_CALLBACK_DATA data, _In_ PCFLT_RELATED_OBJECTS objects, _In_opt_ PVOID context,
    FLT_POST_OPERATION_FLAGS flags, _In_ PFLT_POST_OPERATION_CALLBACK safeRoutine)
{
    FLT_POSTOP_CALLBACK_STATUS result;

    if (KeGetCurrentIrql() <= APC_LEVEL) {
        return safeRoutine(data, objects, context, flags);
    }
    if (FltDoCompletionProcessingWhenSafe(data, objects, context, flags, safeRoutine, &result)) {
        return result;
    }
    if (context != NULL) {
        FreePostCtx((POST_CTX*)context);
    }
    EmitCountDrop();
    return FLT_POSTOP_FINISHED_PROCESSING;
}

/* ---- Create (D3) -------------------------------------------------------------------------------------------------- */

static FLT_PREOP_CALLBACK_STATUS PreCreate(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    UNREFERENCED_PARAMETER(FltObjects);

    *CompletionContext = NULL;

    if (!ScopeStateActive() || FlagOn(Data->Iopb->OperationFlags, SL_OPEN_PAGING_FILE)) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    return FLT_PREOP_SUCCESS_WITH_CALLBACK;
}

static FLT_POSTOP_CALLBACK_STATUS PostCreateSafe(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_opt_ PVOID CompletionContext, FLT_POST_OPERATION_FLAGS Flags)
{
    NTSTATUS status = Data->IoStatus.Status;
    BOOLEAN denied = (status == STATUS_ACCESS_DENIED);
    ULONG options;
    ULONG disposition;
    ULONG createOptions;
    UINT8 operation;
    BOOLEAN wantRecord;
    BOOLEAN wantDelete;
    PFLT_FILE_NAME_INFORMATION name = NULL;
    PIO_SECURITY_CONTEXT securityContext;
    PSECURITY_SUBJECT_CONTEXT subject = NULL;
    WCHAR sid[AUDITOR_SID_CHARS];
    USHORT sidChars = 0;
    ULONG pid;

    UNREFERENCED_PARAMETER(CompletionContext);

    if (FlagOn(Flags, FLTFL_POST_OPERATION_DRAINING) || status == STATUS_REPARSE || (!NT_SUCCESS(status) && !denied)) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }
    if (FltObjects->FileObject == NULL || FlagOn(FltObjects->FileObject->Flags, FO_VOLUME_OPEN)) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    options = Data->Iopb->Parameters.Create.Options;
    disposition = options >> 24;
    createOptions = options & 0x00FFFFFF;

    if (denied) {
        /* The create failed, so only the request is known: a plain create request is reported as Created, anything else as Open. */
        operation = (UINT8)((disposition == FILE_CREATE) ? OP_CREATED : OP_OPEN);
    } else {
        switch ((ULONG)Data->IoStatus.Information) {
        case FILE_CREATED:      operation = (UINT8)OP_CREATED;  break;
        case FILE_OVERWRITTEN:
        case FILE_SUPERSEDED:   operation = (UINT8)OP_MODIFIED; break;
        default:                operation = (UINT8)OP_OPEN;     break;
        }
    }

    /* An existing file or folder opened with read access is a content view when reads are in scope: this one record
     * per open stands in for the plain Open, and the agent merges repeats per user, file and hour to tame read volume. */
    if (operation == (UINT8)OP_OPEN && ScopeStateAllows(AUDIT_OP_BIT(OP_CONTENT_VIEW)) && ReadDataAccess(Data, denied)) {
        operation = (UINT8)OP_CONTENT_VIEW;
    }

    /* A denied attempt is always worth reporting once anything is being audited; the agent filters by session. */
    wantRecord = denied ? ScopeStateActive() : ScopeStateAllows(AUDIT_OP_BIT(operation));
    wantDelete = !denied && FlagOn(createOptions, FILE_DELETE_ON_CLOSE) && ScopeStateAllows(AUDIT_OP_BIT(OP_DELETED));
    if (!wantRecord && !wantDelete) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    if (!NT_SUCCESS(GetAuditName(Data, &name))) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }
    if (!InScope(&name->Name)) {
        FltReleaseFileNameInformation(name);
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    pid = FltGetRequestorProcessId(Data);
    securityContext = Data->Iopb->Parameters.Create.SecurityContext;
    if (securityContext != NULL && securityContext->AccessState != NULL) {
        subject = &securityContext->AccessState->SubjectSecurityContext;
    }
    EmitGetSid(subject, sid, &sidChars);

    if (wantDelete) {
        HANDLE_CTX* handle = GetHandleCtx(FltObjects, TRUE);
        if (handle != NULL) {
            handle->DeletePid = pid;
            handle->DeleteSidChars = sidChars;
            RtlCopyMemory(handle->DeleteSid, sid, sidChars * sizeof(WCHAR));
            InterlockedExchange(&handle->DeleteRequested, 1);
            FltReleaseContext(handle);
        }
    }

    if (wantRecord) {
        UINT8 objectType = ObjectTypeOf(FltObjects, FlagOn(createOptions, FILE_DIRECTORY_FILE) != 0);
        EmitNow(operation, objectType, pid, denied ? (LONG)status : 0, sid, sidChars, name->Name.Buffer, name->Name.Length / sizeof(WCHAR));
    }

    FltReleaseFileNameInformation(name);
    return FLT_POSTOP_FINISHED_PROCESSING;
}

static FLT_POSTOP_CALLBACK_STATUS PostCreate(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_opt_ PVOID CompletionContext, FLT_POST_OPERATION_FLAGS Flags)
{
    return RunWhenSafe(Data, FltObjects, CompletionContext, Flags, PostCreateSafe);
}

/* ---- SetInformation: rename, move and delete (D4) -------------------------------------------------------------------- */

#define RENAME_NAME_OFFSET  ((ULONG)FIELD_OFFSET(FILE_RENAME_INFORMATION, FileName))

static FLT_PREOP_CALLBACK_STATUS PreRename(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    PFILE_RENAME_INFORMATION info = (PFILE_RENAME_INFORMATION)Data->Iopb->Parameters.SetFileInformation.InfoBuffer;
    ULONG length = Data->Iopb->Parameters.SetFileInformation.Length;
    HANDLE rootDirectory = NULL;
    ULONG nameBytes = 0;
    PFLT_FILE_NAME_INFORMATION oldName = NULL;
    PFLT_FILE_NAME_INFORMATION newName = NULL;
    NTSTATUS status = STATUS_UNSUCCESSFUL;
    WCHAR sid[AUDITOR_SID_CHARS];
    USHORT sidChars = 0;
    POST_CTX* ctx;
    ULONG bytes;
    UINT8 operation;

    if (!ScopeStateAllows(AUDIT_OP_BIT(OP_RENAMED) | AUDIT_OP_BIT(OP_MOVED)) || info == NULL
        || length < RENAME_NAME_OFFSET || FltObjects->FileObject == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    __try {
        rootDirectory = info->RootDirectory;
        nameBytes = info->FileNameLength;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    if (nameBytes == 0 || (nameBytes & 1) != 0 || nameBytes > length - RENAME_NAME_OFFSET) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    if (!NT_SUCCESS(GetAuditName(Data, &oldName))) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    __try {
        status = FltGetDestinationFileNameInformation(
            Data->Iopb->TargetInstance, Data->Iopb->TargetFileObject, rootDirectory, info->FileName, nameBytes,
            FLT_FILE_NAME_NORMALIZED | FLT_FILE_NAME_QUERY_DEFAULT, &newName);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }
    if (!NT_SUCCESS(status)) {
        FltReleaseFileNameInformation(oldName);
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    /* Moving a file into or out of a watched folder matters as much as renaming inside it, so either name may match. */
    if (newName->Name.Length == 0 || (newName->Name.Length / sizeof(WCHAR)) > 0x7FFF || (!InScope(&oldName->Name) && !InScope(&newName->Name))) {
        FltReleaseFileNameInformation(newName);
        FltReleaseFileNameInformation(oldName);
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    operation = (UINT8)(SameParent(&oldName->Name, &newName->Name) ? OP_RENAMED : OP_MOVED);
    EmitGetSid(NULL, sid, &sidChars);

    ctx = AllocPostCtx(POST_KIND_RECORDS);
    if (ctx == NULL) {
        EmitCountDrop();
    } else {
        ctx->Record[0] = EmitBuild(
            operation, ObjectTypeOf(FltObjects, FALSE), FltGetRequestorProcessId(Data), 0, sid, sidChars,
            oldName->Name.Buffer, oldName->Name.Length / sizeof(WCHAR),
            newName->Name.Buffer, newName->Name.Length / sizeof(WCHAR), &bytes);
        ctx->Bytes[0] = bytes;
        ctx->Count = 1;
        if (ctx->Record[0] == NULL) {
            FreePostCtx(ctx);
            ctx = NULL;
        }
    }

    FltReleaseFileNameInformation(newName);
    FltReleaseFileNameInformation(oldName);

    if (ctx == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    *CompletionContext = ctx;
    return FLT_PREOP_SUCCESS_WITH_CALLBACK;
}

/* A delete is "requested" here and only counted once the handle is closed with the delete still pending (see PreCleanup),
 * because the request can be undone or fail. The request is passed on so the post-callback knows whether it worked. */
static FLT_PREOP_CALLBACK_STATUS PreDisposition(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    FILE_INFORMATION_CLASS infoClass = Data->Iopb->Parameters.SetFileInformation.FileInformationClass;
    PVOID buffer = Data->Iopb->Parameters.SetFileInformation.InfoBuffer;
    ULONG length = Data->Iopb->Parameters.SetFileInformation.Length;
    BOOLEAN deleteFile = FALSE;
    POST_CTX* ctx;

    if (!ScopeStateAllows(AUDIT_OP_BIT(OP_DELETED)) || buffer == NULL || FltObjects->FileObject == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    __try {
        if (infoClass == FileDispositionInformationEx) {
            deleteFile = length >= sizeof(FILE_DISPOSITION_INFORMATION_EX) && FlagOn(((PFILE_DISPOSITION_INFORMATION_EX)buffer)->Flags, FILE_DISPOSITION_DELETE);
        } else {
            deleteFile = length >= sizeof(FILE_DISPOSITION_INFORMATION) && ((PFILE_DISPOSITION_INFORMATION)buffer)->DeleteFile;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    /* Clearing a pending delete needs no work here: PreCleanup asks the file system whether a delete is still pending. */
    if (!deleteFile) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    ctx = AllocPostCtx(POST_KIND_DISPOSITION);
    if (ctx == NULL) {
        EmitCountDrop();
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    ctx->Pid = FltGetRequestorProcessId(Data);
    EmitGetSid(NULL, ctx->Sid, &ctx->SidChars);

    *CompletionContext = ctx;
    return FLT_PREOP_SUCCESS_WITH_CALLBACK;
}

static FLT_PREOP_CALLBACK_STATUS PreSetInfo(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    *CompletionContext = NULL;

    if (!ScopeStateActive() || KeGetCurrentIrql() > APC_LEVEL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    switch (Data->Iopb->Parameters.SetFileInformation.FileInformationClass) {
    case FileRenameInformation:
    case FileRenameInformationEx:
    case FileRenameInformationBypassAccessCheck:
    case FileRenameInformationExBypassAccessCheck:
        return PreRename(Data, FltObjects, CompletionContext);

    case FileDispositionInformation:
    case FileDispositionInformationEx:
        return PreDisposition(Data, FltObjects, CompletionContext);

    default:
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
}

/* ---- SetSecurity: permission, owner and auditing changes (D4) ----------------------------------------------------- */

#define SEC_OWNER_PARTS     (OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION)
#define SEC_DACL_PARTS      (DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION | UNPROTECTED_DACL_SECURITY_INFORMATION)
#define SEC_SACL_PARTS      (SACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION | ATTRIBUTE_SECURITY_INFORMATION | \
                             SCOPE_SECURITY_INFORMATION | PROTECTED_SACL_SECURITY_INFORMATION | UNPROTECTED_SACL_SECURITY_INFORMATION)

static FLT_PREOP_CALLBACK_STATUS PreSetSecurity(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    SECURITY_INFORMATION parts = Data->Iopb->Parameters.SetSecurity.SecurityInformation;
    UINT8 operations[3];
    ULONG count = 0;
    ULONG i;
    PFLT_FILE_NAME_INFORMATION name = NULL;
    WCHAR sid[AUDITOR_SID_CHARS];
    USHORT sidChars = 0;
    UINT8 objectType;
    ULONG pid;
    POST_CTX* ctx;

    *CompletionContext = NULL;

    if (!ScopeStateActive() || KeGetCurrentIrql() > APC_LEVEL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    /* One record per part of the descriptor that changes. */
    if ((parts & SEC_OWNER_PARTS) && ScopeStateAllows(AUDIT_OP_BIT(OP_OWNER_CHANGE))) {
        operations[count++] = (UINT8)OP_OWNER_CHANGE;
    }
    if ((parts & SEC_DACL_PARTS) && ScopeStateAllows(AUDIT_OP_BIT(OP_PERMISSION_CHANGE))) {
        operations[count++] = (UINT8)OP_PERMISSION_CHANGE;
    }
    if ((parts & SEC_SACL_PARTS) && ScopeStateAllows(AUDIT_OP_BIT(OP_AUDITING_CHANGE))) {
        operations[count++] = (UINT8)OP_AUDITING_CHANGE;
    }
    if (count == 0) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    if (!NT_SUCCESS(GetAuditName(Data, &name))) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    if (!InScope(&name->Name)) {
        FltReleaseFileNameInformation(name);
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    ctx = AllocPostCtx(POST_KIND_RECORDS);
    if (ctx == NULL) {
        FltReleaseFileNameInformation(name);
        EmitCountDrop();
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    pid = FltGetRequestorProcessId(Data);
    EmitGetSid(NULL, sid, &sidChars);
    objectType = ObjectTypeOf(FltObjects, FALSE);

    for (i = 0; i < count; i++) {
        ULONG bytes = 0;
        PUCHAR record = EmitBuild(
            operations[i], objectType, pid, 0, sid, sidChars,
            name->Name.Buffer, name->Name.Length / sizeof(WCHAR), NULL, 0, &bytes);
        if (record != NULL) {
            ctx->Record[ctx->Count] = record;
            ctx->Bytes[ctx->Count] = bytes;
            ctx->Count++;
        }
    }
    FltReleaseFileNameInformation(name);

    if (ctx->Count == 0) {
        FreePostCtx(ctx);
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    *CompletionContext = ctx;
    return FLT_PREOP_SUCCESS_WITH_CALLBACK;
}

/* ---- the post-callback shared by SetInformation and SetSecurity ---------------------------------------------------- */

static FLT_POSTOP_CALLBACK_STATUS PostSetSafe(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_opt_ PVOID CompletionContext, FLT_POST_OPERATION_FLAGS Flags)
{
    POST_CTX* ctx = (POST_CTX*)CompletionContext;
    NTSTATUS status = Data->IoStatus.Status;
    BOOLEAN denied = (status == STATUS_ACCESS_DENIED);
    ULONG i;

    if (ctx == NULL) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    if (!FlagOn(Flags, FLTFL_POST_OPERATION_DRAINING) && (NT_SUCCESS(status) || denied)) {
        if (ctx->Kind == POST_KIND_RECORDS) {
            for (i = 0; i < ctx->Count; i++) {
                AuditPatchStatus(ctx->Record[i], (int32_t)(denied ? status : 0));
                EmitRecord(ctx->Record[i], ctx->Bytes[i]);
            }
        } else if (ctx->Kind == POST_KIND_DISPOSITION) {
            if (NT_SUCCESS(status)) {
                HANDLE_CTX* handle = GetHandleCtx(FltObjects, TRUE);
                if (handle != NULL) {
                    handle->DeletePid = ctx->Pid;
                    handle->DeleteSidChars = ctx->SidChars;
                    RtlCopyMemory(handle->DeleteSid, ctx->Sid, ctx->SidChars * sizeof(WCHAR));
                    InterlockedExchange(&handle->DeleteRequested, 1);
                    FltReleaseContext(handle);
                }
            } else {
                /* The delete itself was refused: nothing will be pending at cleanup, so report the attempt now. */
                PFLT_FILE_NAME_INFORMATION name = NULL;
                if (NT_SUCCESS(GetAuditName(Data, &name))) {
                    if (InScope(&name->Name)) {
                        EmitNow(
                            (UINT8)OP_DELETED, ObjectTypeOf(FltObjects, FALSE), ctx->Pid, (LONG)status,
                            ctx->Sid, ctx->SidChars, name->Name.Buffer, name->Name.Length / sizeof(WCHAR));
                    }
                    FltReleaseFileNameInformation(name);
                }
            }
        }
    }

    FreePostCtx(ctx);
    return FLT_POSTOP_FINISHED_PROCESSING;
}

static FLT_POSTOP_CALLBACK_STATUS PostSet(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _In_opt_ PVOID CompletionContext, FLT_POST_OPERATION_FLAGS Flags)
{
    return RunWhenSafe(Data, FltObjects, CompletionContext, Flags, PostSetSafe);
}

/* ---- Write: Modified (D4) ----------------------------------------------------------------------------------------- */

static FLT_PREOP_CALLBACK_STATUS PreWrite(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    HANDLE_CTX* handle;
    PFLT_FILE_NAME_INFORMATION name = NULL;
    WCHAR sid[AUDITOR_SID_CHARS];
    USHORT sidChars = 0;

    *CompletionContext = NULL;

    if (!ScopeStateAllows(AUDIT_OP_BIT(OP_MODIFIED)) || KeGetCurrentIrql() > APC_LEVEL
        || FlagOn(Data->Iopb->IrpFlags, IRP_PAGING_IO) || Data->Iopb->Parameters.Write.Length == 0
        || FltObjects->FileObject == NULL || FlagOn(FltObjects->FileObject->Flags, FO_VOLUME_OPEN)) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    /* One Modified per open handle is enough: a long copy would otherwise send a record per write, and the agent merges
     * repeated writes anyway. Writes that go through a memory mapping arrive as paging I/O and are not seen here. */
    handle = GetHandleCtx(FltObjects, TRUE);
    if (handle != NULL) {
        BOOLEAN already = (InterlockedCompareExchange(&handle->ModifiedReported, 1, 0) != 0);
        FltReleaseContext(handle);
        if (already) {
            return FLT_PREOP_SUCCESS_NO_CALLBACK;
        }
    }

    if (NT_SUCCESS(GetAuditName(Data, &name))) {
        if (InScope(&name->Name)) {
            EmitGetSid(NULL, sid, &sidChars);
            EmitNow((UINT8)OP_MODIFIED, (UINT8)OBJ_FILE, FltGetRequestorProcessId(Data), 0, sid, sidChars, name->Name.Buffer, name->Name.Length / sizeof(WCHAR));
        }
        FltReleaseFileNameInformation(name);
    }
    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

/* ---- Cleanup: Deleted (D4) ---------------------------------------------------------------------------------------- */

/* Windows does not allow name queries after cleanup, so the delete is confirmed before the file system processes it:
 * this handle asked for it, and the file system still says a delete is pending. */
static FLT_PREOP_CALLBACK_STATUS PreCleanup(
    _Inout_ PFLT_CALLBACK_DATA Data, _In_ PCFLT_RELATED_OBJECTS FltObjects, _Flt_CompletionContext_Outptr_ PVOID* CompletionContext)
{
    HANDLE_CTX* handle;
    FILE_STANDARD_INFORMATION standard;
    ULONG returned = 0;
    PFLT_FILE_NAME_INFORMATION name = NULL;
    WCHAR sid[AUDITOR_SID_CHARS];
    USHORT sidChars;
    ULONG pid;

    *CompletionContext = NULL;

    if (!ScopeStateAllows(AUDIT_OP_BIT(OP_DELETED)) || KeGetCurrentIrql() > APC_LEVEL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    handle = GetHandleCtx(FltObjects, FALSE);
    if (handle == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    if (InterlockedExchange(&handle->DeleteRequested, 0) == 0) {
        FltReleaseContext(handle);
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }
    pid = handle->DeletePid;
    sidChars = handle->DeleteSidChars;
    RtlCopyMemory(sid, handle->DeleteSid, sidChars * sizeof(WCHAR));
    FltReleaseContext(handle);

    if (!NT_SUCCESS(FltQueryInformationFile(FltObjects->Instance, FltObjects->FileObject, &standard, sizeof(standard), FileStandardInformation, &returned))
        || !standard.DeletePending) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;        /* the request was withdrawn or never took effect */
    }

    if (NT_SUCCESS(GetAuditName(Data, &name))) {
        if (InScope(&name->Name)) {
            EmitNow(
                (UINT8)OP_DELETED, standard.Directory ? (UINT8)OBJ_FOLDER : (UINT8)OBJ_FILE, pid, 0,
                sid, sidChars, name->Name.Buffer, name->Name.Length / sizeof(WCHAR));
        }
        FltReleaseFileNameInformation(name);
    }
    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

/* ---- registration ------------------------------------------------------------------------------------------------- */

CONST FLT_OPERATION_REGISTRATION gOperationCallbacks[] = {
    { IRP_MJ_CREATE,          0,                                            PreCreate,      PostCreate },
    { IRP_MJ_SET_INFORMATION, 0,                                            PreSetInfo,     PostSet },
    { IRP_MJ_SET_SECURITY,    0,                                            PreSetSecurity, PostSet },
    { IRP_MJ_WRITE,           FLTFL_OPERATION_REGISTRATION_SKIP_PAGING_IO,  PreWrite,       NULL },
    { IRP_MJ_CLEANUP,         0,                                            PreCleanup,     NULL },
    { IRP_MJ_OPERATION_END }
};
