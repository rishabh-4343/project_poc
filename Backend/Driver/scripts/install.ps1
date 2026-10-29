# Installs the test-signed driver on a TEST VM. Run from an elevated PowerShell inside the VM, from the folder that holds the
# package files (AuditorDriver.sys/.inf/.cat and wfsa-test-signing.cer). It does not enable test signing and does not reboot: it refuses
# to continue unless the VM is already in test-signing mode.
#   Never run this on a developer or production machine.
param(
    [switch]$IUnderstandThisInstallsAKernelDriver,
    [switch]$AllowPhysicalMachine,   # only for a dedicated, disposable test box
    [string]$PackageDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

if (-not $IUnderstandThisInstallsAKernelDriver) {
    throw 'Refusing to run. Pass -IUnderstandThisInstallsAKernelDriver inside a disposable test VM.'
}

$cs = Get-CimInstance Win32_ComputerSystem
$byModel = $cs.Model -match 'Virtual|VMware|VirtualBox|KVM|HVM|Bochs'
$byMaker = $cs.Manufacturer -match 'QEMU|Xen|innotek|Parallels|VMware'
if (-not ($byModel -or $byMaker) -and -not $AllowPhysicalMachine) {
    throw "This does not look like a virtual machine (manufacturer '$($cs.Manufacturer)', model '$($cs.Model)'). Install only in a VM, or pass -AllowPhysicalMachine on a disposable test box."
}

if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
    throw 'Run from an elevated PowerShell.'
}

$boot = (bcdedit /enum '{current}') -join "`n"
if ($boot -notmatch '(?m)^testsigning\s+Yes') {
    throw 'Test signing is off. In the VM run:  bcdedit /set testsigning on   then reboot, and run this script again.'
}

foreach ($f in 'AuditorDriver.sys', 'AuditorDriver.inf', 'AuditorDriver.cat', 'wfsa-test-signing.cer') {
    if (-not (Test-Path (Join-Path $PackageDir $f))) { throw "Missing $f in $PackageDir" }
}

# Trust the test certificate for this VM only.
$cer = Join-Path $PackageDir 'wfsa-test-signing.cer'
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

# DefaultInstall copies the .sys to System32\drivers and registers the service and the minifilter instance.
$inf = Join-Path $PackageDir 'AuditorDriver.inf'
& rundll32.exe setupapi.dll,InstallHinfSection DefaultInstall 132 $inf
Start-Sleep -Seconds 2

if (-not (Get-Service AuditorDriver -ErrorAction SilentlyContinue)) { throw 'Install did not create the AuditorDriver service (check %windir%\inf\setupapi.dev.log).' }
Write-Host 'Installed. Load it with:  fltmc load AuditorDriver    then check:  fltmc filters'
Write-Host 'Unload with:              fltmc unload AuditorDriver'
