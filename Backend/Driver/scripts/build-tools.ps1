# Builds the user-mode test tool tests\AuditorPortTool.c into build\tools\AuditorPortTool.exe. Copy it into the test VM with the package.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $root 'build\tools'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'vcenv.ps1') | Out-Null }

& cl.exe /nologo /W4 /WX /O2 /D_CRT_SECURE_NO_WARNINGS /I"$root\AuditorDriver" "/Fo:$out\\" "/Fe:$out\AuditorPortTool.exe" `
    "$root\tests\AuditorPortTool.c" /link fltlib.lib
if ($LASTEXITCODE -ne 0) { throw 'AuditorPortTool failed to compile' }
Write-Host "OK  $out\AuditorPortTool.exe"
