# Compiles and links the kernel minifilter (AuditorDriver.sys) against the WDK in Backend/Driver/.wdk.
# Flags mirror what the WDK's MSBuild targets apply to an x64 kernel-mode driver (WindowsDriver.*.props). Our own
# sources build at /W4 /WX; the WDK header tree is marked external so Microsoft's headers (which raise C4324 with this
# compiler) do not fail the build while any warning in our code still does.
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$Wdk = ''
)
$ErrorActionPreference = 'Stop'

$driverRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $driverRoot 'AuditorDriver'
$out = Join-Path $driverRoot "build\driver\$Configuration"
$wdkRoot = if ($Wdk) { $Wdk } else { Join-Path $driverRoot '.wdk\pkg\c' }
$ver = '10.0.26100.0'
$inc = Join-Path $wdkRoot "Include\$ver"
$lib = Join-Path $wdkRoot "Lib\$ver\km\x64"

foreach ($p in @($inc, $lib)) { if (-not (Test-Path $p)) { throw "WDK path not found: $p (is the .wdk NuGet package restored?)" } }

# The WDK package depends on Microsoft.Windows.SDK.CPP.x64 for the shared headers (specstrings.h, sdkddkver.h, ntstatus.h).
# That package is not restored into .wdk, so the installed Windows SDK of the same version supplies them.
$sdkShared = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Include\$ver\shared"
if (-not (Test-Path (Join-Path $sdkShared 'specstrings.h'))) {
    throw "Shared SDK headers for $ver not found at $sdkShared. Install the Windows SDK $ver or restore Microsoft.Windows.SDK.CPP.x64 into .wdk."
}

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue) -or -not $env:VCToolsInstallDir) {
    . (Join-Path $PSScriptRoot 'vcenv.ps1') | Out-Null
}
$vcInclude = Join-Path $env:VCToolsInstallDir 'include'     # stdint.h and the compiler's own headers

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$defines = @(
    '/DAUDITOR_KERNEL', '/D_WIN64', '/D_AMD64_=1', '/DAMD64', '/DSTD_CALL', '/DDEPRECATE_DDK_FUNCTIONS=1',
    '/DNTDDI_VERSION=0x0A000000', '/D_WIN32_WINNT=0x0A00', '/DWINVER=0x0A00',   # Windows 10 / Server 2016 and later
    '/DPOOL_NX_OPTIN=1', '/D_NO_CRT_STDIO_INLINE=1'
)
$defines += $(if ($Configuration -eq 'Debug') { '/DDBG=1', '/D_DEBUG' } else { '/DDBG=0', '/DNDEBUG' })

$cl = @(
    '/nologo', '/c', '/kernel', '/X',                               # /X: ignore the user-mode SDK include path vcvars set
    '/W4', '/WX',
    '/wd4603', '/wd4627', '/wd4986', '/wd4987', '/wd4748',          # the warnings the WDK itself disables for drivers
    '/GS', '/Gz', '/Gy', '/GF', '/Gm-', '/GR-', '/Zc:wchar_t', '/Zc:forScope', '/Zc:inline', '/Zp8',
    '-cbstring', '-d2epilogunwind',
    '/Zi', "/Fd$out\AuditorDriver.pdb",
    "/I$src", "/I$inc\km", "/I$inc\shared", "/I$inc\km\crt", "/I$sdkShared", "/I$vcInclude",
    "/external:I$inc", "/external:I$sdkShared", "/external:I$vcInclude", '/external:W0'
)
$cl += $(if ($Configuration -eq 'Debug') { '/Od', '/homeparams' } else { '/O2', '/Oi' })

$objects = @()
foreach ($file in Get-ChildItem $src -Filter '*.c' | Sort-Object Name) {
    $obj = Join-Path $out ($file.BaseName + '.obj')
    Write-Host "cl  $($file.Name)"
    & cl.exe @cl @defines "/Fo$obj" $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $($file.Name)" }
    $objects += $obj
}

$sys = Join-Path $out 'AuditorDriver.sys'
$libs = 'ntoskrnl.lib', 'hal.lib', 'fltMgr.lib', 'BufferOverflowFastFailK.lib', 'ntstrsafe.lib', 'wmilib.lib'
$link = @(
    '/nologo', '/DRIVER', '/SUBSYSTEM:NATIVE', '/ENTRY:GsDriverEntry', '/NODEFAULTLIB', '/KERNEL',
    '/MERGE:_TEXT=.text', '/MERGE:_PAGE=PAGE', '/OPT:REF', '/OPT:ICF', '/RELEASE', '/WX',
    '/DEBUG', "/PDB:$out\AuditorDriver.pdb", '/IGNORE:4198,4010,4037,4039,4065,4070,4078,4087,4089,4221,4108,4088,4218,4235',
    "/LIBPATH:$lib", "/OUT:$sys"
)
Write-Host "link AuditorDriver.sys ($Configuration)"
& link.exe @link @objects @libs
if ($LASTEXITCODE -ne 0) { throw 'link failed' }

# ---- checks on the finished image -------------------------------------------------------------------------------
$headers = (& dumpbin.exe /headers $sys) -join "`n"
if ($headers -notmatch 'subsystem \(Native\)') { throw 'image is not a native-subsystem driver' }
if ($headers -notmatch '8664 machine \(x64\)') { throw 'image is not x64' }

$imports = (& dumpbin.exe /imports $sys) | Where-Object { $_ -match '^\s+\S+\.(dll|sys|exe)\s*$' } | ForEach-Object { $_.Trim().ToLowerInvariant() }
$allowed = 'ntoskrnl.exe', 'fltmgr.sys', 'hal.dll'
$bad = @($imports | Where-Object { $_ -notin $allowed })
if ($bad.Count) { throw "unexpected imports (a kernel driver may only import kernel modules): $($bad -join ', ')" }

Write-Host ("OK  {0}  ({1:N0} bytes)  imports: {2}" -f $sys, (Get-Item $sys).Length, ($imports -join ', '))
