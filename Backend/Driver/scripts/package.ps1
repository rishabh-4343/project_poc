# Builds the driver and assembles an installable package in build\package: AuditorDriver.sys, AuditorDriver.inf (stamped)
# and AuditorDriver.cat (catalog). Run sign-test.ps1 afterwards to test-sign it. Nothing here loads or installs anything.
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pkg = Join-Path $root 'build\package'
$ver = '10.0.26100.0'
$wdkBin = Join-Path $root ".wdk\pkg\c\bin\$ver"
$stampinf = Join-Path $wdkBin 'x64\stampinf.exe'
$inf2cat = Join-Path $wdkBin 'x86\Inf2Cat.exe'
foreach ($t in @($stampinf, $inf2cat)) { if (-not (Test-Path $t)) { throw "WDK tool not found: $t" } }

& (Join-Path $PSScriptRoot 'build-driver.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'driver build failed' }

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null
Copy-Item (Join-Path $root "build\driver\$Configuration\AuditorDriver.sys") $pkg
Copy-Item (Join-Path $root "build\driver\$Configuration\AuditorDriver.pdb") $pkg
Copy-Item (Join-Path $root 'AuditorDriver\AuditorDriver.inf') $pkg

# The user-mode test tool travels with the package so the VM has everything in one folder.
& (Join-Path $PSScriptRoot 'build-tools.ps1')
if ($LASTEXITCODE -ne 0) { throw 'test tool build failed' }
Copy-Item (Join-Path $root 'build\tools\AuditorPortTool.exe') $pkg
foreach ($s in 'install.ps1', 'uninstall.ps1') { Copy-Item (Join-Path $PSScriptRoot $s) $pkg }

$inf = Join-Path $pkg 'AuditorDriver.inf'
& $stampinf -f $inf -a amd64 -d '*' -v '*'
if ($LASTEXITCODE -ne 0) { throw 'stampinf failed' }

# Inf2Cat reads the INF, hashes the files it lists and writes the catalog; it fails on an INF it cannot parse, which makes
# it the INF check as well. 10_X64 = Windows 10 and later, x64.
& $inf2cat "/driver:$pkg" '/os:10_X64' '/verbose'
if ($LASTEXITCODE -ne 0) { throw 'Inf2Cat failed' }

Get-ChildItem $pkg | ForEach-Object { Write-Host ('  {0,-22} {1,9:N0} bytes' -f $_.Name, $_.Length) }
Write-Host "Package ready: $pkg  (next: sign-test.ps1)"
