# Removes the driver from a TEST VM: unloads it, deletes the service and file, and removes the test certificate from the VM.
# Run elevated inside the VM. Does not touch the test-signing boot setting.
param([switch]$IUnderstandThisRemovesAKernelDriver, [string]$PackageDir = $PSScriptRoot)
$ErrorActionPreference = 'Continue'

if (-not $IUnderstandThisRemovesAKernelDriver) { throw 'Pass -IUnderstandThisRemovesAKernelDriver to confirm.' }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { throw 'Run from an elevated PowerShell.' }

if ((fltmc filters) -match 'AuditorDriver') {
    fltmc unload AuditorDriver
    if ($LASTEXITCODE -ne 0) { Write-Warning 'fltmc unload failed (an agent may still hold the port). Stop the agent and retry, or reboot the VM.' }
}

$inf = Join-Path $PackageDir 'AuditorDriver.inf'
if (Test-Path $inf) { & rundll32.exe setupapi.dll,InstallHinfSection DefaultUninstall 132 $inf; Start-Sleep -Seconds 2 }

if (Get-Service AuditorDriver -ErrorAction SilentlyContinue) { sc.exe delete AuditorDriver | Out-Null }
Remove-Item "$env:windir\System32\drivers\AuditorDriver.sys" -Force -ErrorAction SilentlyContinue

foreach ($store in 'Root', 'TrustedPublisher') {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Subject -eq 'CN=wfsa Test Signing (not for production)' } | Remove-Item
}
Write-Host 'Removed.'
