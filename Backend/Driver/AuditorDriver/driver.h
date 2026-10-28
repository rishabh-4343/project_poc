/* driver.h — shared declarations for the kernel driver sources.
 * The portable core (scope.c, record.c) is built into the same image but does not include this file. */
#ifndef AUDITOR_DRIVER_H
#define AUDITOR_DRIVER_H

#ifndef AUDITOR_KERNEL
#error "driver sources must be built with /DAUDITOR_KERNEL (scripts/build-driver.ps1 does this)"
#endif

/* Lets zeroed non-paged allocations work on Windows 10 releases before 2004 (ExAllocatePoolZero zeroes the memory itself
 * there). Every allocation goes through AudAlloc and its result is checked. */
#define POOL_ZERO_DOWN_LEVEL_SUPPORT

#include <fltKernel.h>

#define AudAlloc(bytes)     ExAllocatePoolZero(NonPagedPoolNx, (bytes), AUDITOR_POOL_TAG)

#ifndef ALLOC_PRAGMA
#define ALLOC_PRAGMA 1
#endif

/* Pool tag for this module ("wfsa"); the tag is stored reversed, so poolmon shows it as wfsa. */
#define AUDITOR_POOL_TAG        'asfw'

/* The requestor SID goes into a record as text; 68 characters is the size the design fixes for it. A longer SID is sent as empty. */
#define AUDITOR_SID_CHARS       68

/* Largest record handed to the agent. The agent's FilterGetMessage buffer must hold this plus FILTER_MESSAGE_HEADER. */
#define AUDITOR_MAX_RECORD_BYTES (64 * 1024)

extern PFLT_FILTER gFilter;

/* ---- scopestate.c: the watch scope the agent last sent ------------------------------------------------------------ */
VOID    ScopeStateInit(VOID);
VOID    ScopeStateCleanup(VOID);
/* Takes ownership of `blob` (a pool block holding the validated message); `folders` points inside it. */
VOID    ScopeStateSet(_In_opt_ PUCHAR blob, _In_opt_ const UCHAR* folders, UINT32 folderCount, UINT32 operationMask);
VOID    ScopeStateGet(_Out_ UINT32* operationMask, _Out_ UINT32* folderCount);
BOOLEAN ScopeStateActive(VOID);                                     /* any folder and any operation? */
BOOLEAN ScopeStateAllows(UINT32 operationBits);                     /* mask & bits != 0 */
BOOLEAN ScopeStateMatch(_In_reads_(chars) const WCHAR* path, SIZE_T chars);

/* ---- emit.c: record transport to the agent ---------------------------------------------------------------------- */
VOID    EmitInit(VOID);
UINT64  EmitSentTotal(VOID);
UINT64  EmitDroppedTotal(VOID);
VOID    EmitCountDrop(VOID);
VOID    EmitNoteReconnect(VOID);
/* Sends one finished record (never blocks); counts a drop and arms the overflow notice when it cannot be delivered. */
VOID    EmitRecord(_In_reads_bytes_(bytes) const UCHAR* record, ULONG bytes);
/* Build a record into a new pool block; the caller frees it with EmitFree. Returns NULL (and counts a drop) on failure. */
PUCHAR  EmitBuild(
    UINT8 operation, UINT8 objectType, ULONG processId, LONG status,
    _In_reads_(sidChars) const WCHAR* sid, SIZE_T sidChars,
    _In_reads_(pathChars) const WCHAR* path, SIZE_T pathChars,
    _In_reads_opt_(newChars) const WCHAR* newPath, SIZE_T newChars,
    _Out_ ULONG* bytes);
VOID    EmitFree(_In_ PUCHAR record);
/* Build and send in one step (status already known). */
VOID    EmitNow(
    UINT8 operation, UINT8 objectType, ULONG processId, LONG status,
    _In_reads_(sidChars) const WCHAR* sid, SIZE_T sidChars,
    _In_reads_(pathChars) const WCHAR* path, SIZE_T pathChars);
/* The requestor's SID as text. `subject` is a captured subject context, or NULL to capture the current thread's. */
VOID    EmitGetSid(_In_opt_ PSECURITY_SUBJECT_CONTEXT subject, _Out_writes_(AUDITOR_SID_CHARS) WCHAR* sid, _Out_ USHORT* sidChars);

/* ---- comms.c: communication port ------------------------------------------------------------------------------- */
NTSTATUS CommsStart(VOID);
VOID     CommsStop(VOID);
NTSTATUS CommsSend(_In_reads_bytes_(bytes) PVOID message, ULONG bytes);   /* zero-timeout FltSendMessage */

/* ---- capture.c: operation callbacks ---------------------------------------------------------------------------- */
extern CONST FLT_OPERATION_REGISTRATION gOperationCallbacks[];
extern CONST FLT_CONTEXT_REGISTRATION   gContextRegistration[];

#endif /* AUDITOR_DRIVER_H */
