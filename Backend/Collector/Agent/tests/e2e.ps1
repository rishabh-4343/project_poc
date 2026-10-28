# End to end: simulated driver -> agent -> backend -> ClickHouse, including a backend outage and a hard agent kill.
# Reuses the backend test helpers, so it deletes data in the local dev databases and needs -Yes.
param([switch]$Yes)
if (-not $Yes) { Write-Host 'This resets the local dev databases (see Backend/Auditing/server/tests/run-all.ps1). Re-run with -Yes.'; exit 2 }
$env:AUDITOR_TESTS_OK = '1'
. (Join-Path $PSScriptRoot '..\..\..\Auditing\server\tests\Common.ps1')

$agentRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
dotnet build (Join-Path $agentRoot 'Agent.slnx') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'agent build failed' }
$agentBin = Join-Path $agentRoot 'Agent\bin\Debug\net10.0-windows'
$dataDir = Join-Path $env:TEMP ("agent-e2e-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:agentProc = $null

function Start-Agent($key, $serverId) {
    $e = @{ Agent__BackendUrl = $script:Base; Agent__ApiKey = $key; Agent__ServerId = $serverId; Agent__TenantId = $script:DevTenant
            Agent__DataDirectory = $dataDir; Agent__SimulatedIntervalMs = '200'; Agent__BatchMaxMs = '500'; Agent__HeartbeatSeconds = '2' }
    foreach ($k in $e.Keys) { Set-Item "env:$k" $e[$k] }
    $p = Start-Process (Join-Path $agentBin 'Auditor.Agent.exe') -WorkingDirectory $agentBin -PassThru -WindowStyle Hidden
    foreach ($k in $e.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
    $p
}
function Stop-Agent { if ($script:agentProc) { Stop-Process -Id $script:agentProc.Id -Force -ErrorAction SilentlyContinue; $script:agentProc.WaitForExit(5000) | Out-Null } }
function Wait-Until($seconds, [scriptblock]$cond) { for ($i = 0; $i -lt $seconds * 2; $i++) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }; $false }
function Outbox-Count { @(Get-ChildItem (Join-Path $dataDir 'outbox') -Filter *.batch -ErrorAction SilentlyContinue).Count }

Reset-Data
$app = $null
try {
    $app = Start-App
    $key = $app.Key   # printed only when the dev agent is first seeded
    $fx = New-Fixture; $sid = $fx.ServerId
    $rows = { [int](Q "select count() from wfsa.actions where server_id='$sid'") }
    $max = { [int](Q "select max(seq) from wfsa.actions where server_id='$sid'") }

    Write-Host '== connect and start a session'
    $script:agentProc = Start-Agent $key $sid
    Assert-That 'the agent connects' (Wait-Until 20 { @((Http GET '/api/agents').j)[0].online -eq $true })
    $s = (Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = 'e2e'; operations = @('Created', 'Modified', 'Renamed', 'Deleted'); folders = @(@{ localPath = 'D:\Finance' }) }).j
    Assert-That 'the session becomes Active (the agent accepted StartScope)' (Wait-Until 15 { (P "select state from wfsa.sessions where id='$($s.id)'") -eq 'Active' })
    Assert-That 'actions reach ClickHouse' (Wait-Until 15 { (& $rows) -ge 5 })
    Assert-Eq 'no duplicates and no gaps in seq' (Q "select count() = uniqExact(seq) and max(seq) - min(seq) + 1 = count() from wfsa.actions where server_id='$sid'") 1
    Assert-That 'the row carries enrichment' ((Q "select count() from wfsa.actions where server_id='$sid' and path_hash != 0 and actor_sid != ''") -ge 1)
    Assert-That 'the heartbeat updated the agent version' (Wait-Until 10 { (P "select coalesce(agent_version,'') from core.agents where name='dev-agent'") -ne '' })
    Assert-That 'the heartbeat updated the position' (Wait-Until 10 { (P "select count(*) from wfsa.agent_positions where server_id='$sid'") -eq '1' })

    Write-Host '== backend outage'
    Stop-App $app
    $before = & $rows
    Assert-That 'the outbox grows while the backend is down' (Wait-Until 20 { (Outbox-Count) -ge 2 })
    $app = Start-App -KeepAgent
    Assert-That 'the outbox drains after the backend returns' (Wait-Until 40 { (Outbox-Count) -eq 0 })
    Start-Sleep -Seconds 2
    Assert-That 'everything that was buffered arrived' ((& $rows) -gt $before + 5)
    Assert-Eq 'still no duplicates and no gaps' (Q "select count() = uniqExact(seq) and max(seq) - min(seq) + 1 = count() from wfsa.actions where server_id='$sid'") 1

    Write-Host '== hard kill of the agent'
    Stop-Agent
    Start-Sleep -Seconds 2
    $maxBefore = & $max
    $script:agentProc = Start-Agent $key $sid
    Assert-That 'new actions arrive after the restart (the backend resent the scope)' (Wait-Until 30 { (& $max) -gt $maxBefore + 3 })
    Assert-Eq 'no seq was reused' (Q "select count() = uniqExact(seq) from wfsa.actions where server_id='$sid'") 1
    Assert-That 'the agent resumed above the old high-water mark' ([int](Q "select min(seq) from wfsa.actions where server_id='$sid' and seq > $maxBefore") -gt $maxBefore)

    Write-Host '== stop'
    Http POST "/api/wfsa/sessions/$($s.id)/stop" | Out-Null
    Assert-That 'the session completes (the agent accepted StopScope)' (Wait-Until 15 { (P "select state from wfsa.sessions where id='$($s.id)'") -eq 'Completed' })
    Start-Sleep -Seconds 4
    $settled = & $rows
    Start-Sleep -Seconds 3
    Assert-Eq 'no new actions once the scope is stopped' (& $rows) $settled
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally {
    Stop-Agent
    Stop-App $app
    if ($script:Failures) { Get-ChildItem (Join-Path $dataDir 'logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content -Tail 15 }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Reset-Data
}
Finish-Tests 'agent e2e'
