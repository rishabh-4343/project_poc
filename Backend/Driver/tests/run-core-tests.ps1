# Builds and runs the user-mode checks of the driver's portable core. No WDK or driver loading needed.
# With -Samples <file> it also writes records for the C# parser to verify (see Agent.Tests).
param([string]$Samples = '')
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\scripts\vcenv.ps1')
$out = Join-Path $PSScriptRoot '..\build\tests'
New-Item -ItemType Directory -Force $out | Out-Null

$src = Join-Path $PSScriptRoot '..\AuditorDriver'
cl /nologo /W4 /WX /Fe:"$out\core_tests.exe" /Fo:"$out\\" "$PSScriptRoot\core_tests.c" "$src\scope.c" "$src\record.c"
if ($LASTEXITCODE -ne 0) { throw 'core tests failed to compile' }

if ($Samples) { & "$out\core_tests.exe" $Samples } else { & "$out\core_tests.exe" }
exit $LASTEXITCODE
