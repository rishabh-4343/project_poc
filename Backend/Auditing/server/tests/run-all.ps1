# Builds the server, then runs every test script in order. The tests delete data in the local dev databases.
param([switch]$Yes)

if (-not $Yes) {
    Write-Host 'These tests delete all servers, sessions, commands and agents and truncate the wfsa ClickHouse tables in the local dev databases (docker containers auditor-pg and auditor-ch).'
    Write-Host 'Re-run with -Yes to continue.'
    exit 2
}
$env:AUDITOR_TESTS_OK = '1'

dotnet build (Join-Path $PSScriptRoot '..\server.slnx') --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }

$failed = @()
foreach ($script in Get-ChildItem $PSScriptRoot -Filter '0*.ps1' | Sort-Object Name) {
    Write-Host "`n##### $($script.Name)" -ForegroundColor Cyan
    pwsh -NoProfile -File $script.FullName
    if ($LASTEXITCODE -ne 0) { $failed += $script.Name }
}

Write-Host ''
if ($failed.Count) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host 'ALL TESTS PASSED' -ForegroundColor Green
