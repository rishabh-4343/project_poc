/* scopestate.c — D2: the scope the agent sent, guarded by a push lock. Matching itself is the portable scope.c.
 * Readers (every callback) take the lock shared; only a SET_SCOPE message takes it exclusive, and the old blob is freed
 * after the lock is released. */
#include "driver.h"
#include "scope.h"

typedef struct _SCOPE_STATE {
    EX_PUSH_LOCK    Lock;
    PUCHAR          Blob;           /* owns the memory `Folders` points into */
    const UCHAR*    Folders;
    UINT32          FolderCount;
    UINT32          OperationMask;
} SCOPE_STATE;

static SCOPE_STATE gScope;

VOID ScopeStateInit(VOID)
{
    RtlZeroMemory(&gScope, sizeof(gScope));
    FltInitializePushLock(&gScope.Lock);
}

VOID ScopeStateCleanup(VOID)
{
    if (gScope.Blob != NULL) {
        ExFreePoolWithTag(gScope.Blob, AUDITOR_POOL_TAG);
    }
    FltDeletePushLock(&gScope.Lock);
    RtlZeroMemory(&gScope, sizeof(gScope));
}

VOID ScopeStateSet(_In_opt_ PUCHAR blob, _In_opt_ const UCHAR* folders, UINT32 folderCount, UINT32 operationMask)
{
    PUCHAR old;

    FltAcquirePushLockExclusive(&gScope.Lock);
    old = gScope.Blob;
    gScope.Blob = blob;
    gScope.Folders = folders;
    gScope.FolderCount = folderCount;
    gScope.OperationMask = operationMask;
    FltReleasePushLock(&gScope.Lock);

    if (old != NULL) {
        ExFreePoolWithTag(old, AUDITOR_POOL_TAG);
    }
}

VOID ScopeStateGet(_Out_ UINT32* operationMask, _Out_ UINT32* folderCount)
{
    FltAcquirePushLockShared(&gScope.Lock);
    *operationMask = gScope.OperationMask;
    *folderCount = gScope.FolderCount;
    FltReleasePushLock(&gScope.Lock);
}

BOOLEAN ScopeStateActive(VOID)
{
    BOOLEAN active;

    FltAcquirePushLockShared(&gScope.Lock);
    active = (gScope.FolderCount != 0 && gScope.OperationMask != 0);
    FltReleasePushLock(&gScope.Lock);
    return active;
}

BOOLEAN ScopeStateAllows(UINT32 operationBits)
{
    BOOLEAN allowed;

    FltAcquirePushLockShared(&gScope.Lock);
    allowed = (gScope.FolderCount != 0 && (gScope.OperationMask & operationBits) != 0);
    FltReleasePushLock(&gScope.Lock);
    return allowed;
}

BOOLEAN ScopeStateMatch(_In_reads_(chars) const WCHAR* path, SIZE_T chars)
{
    BOOLEAN match = FALSE;

    FltAcquirePushLockShared(&gScope.Lock);
    if (gScope.FolderCount != 0) {
        match = ScopeMatchPath(gScope.Folders, gScope.FolderCount, path, chars) != 0;
    }
    FltReleasePushLock(&gScope.Lock);
    return match;
}
