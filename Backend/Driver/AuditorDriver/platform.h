/* platform.h — the few things the portable core (scope.c, record.c) needs from its environment.
 * In the driver (AUDITOR_KERNEL defined) they come from the WDK; in the user-mode tests from the C runtime. */
#ifndef AUDITOR_PLATFORM_H
#define AUDITOR_PLATFORM_H

#ifdef AUDITOR_KERNEL
#include <fltKernel.h>
#include <stdint.h>
#define AudUpcase(c)        RtlUpcaseUnicodeChar((WCHAR)(c))
#define AudCopy(d, s, n)    RtlCopyMemory((d), (s), (n))
typedef WCHAR               aud_wchar;
#else
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <wchar.h>
#define AudUpcase(c)        ((wchar_t)towupper((wint_t)(c)))
#define AudCopy(d, s, n)    memcpy((d), (s), (n))
typedef wchar_t             aud_wchar;
#endif

#endif /* AUDITOR_PLATFORM_H */
