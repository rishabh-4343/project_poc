# Loads the Visual Studio C++ build environment (cl, link, ...) into the current PowerShell session.
# Prefers VS 2022 (the toolset the WDK 10.0.26100 packages are validated with), then any other install with the C++ tools.
param([string]$Arch = 'x64')

$env:PATH += ';' + (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer')   # vcvarsall looks for vswhere on PATH
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = & $vswhere -latest -version '[17.0,18.0)' -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $install) { $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $install) { throw 'No Visual Studio with the C++ tools (VC.Tools.x86.x64) was found.' }

$vcvars = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
$dump = cmd /c "`"$vcvars`" $Arch >nul && set"
foreach ($line in $dump) {
    if ($line -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
}
Write-Host "C++ environment: $install ($Arch)"
