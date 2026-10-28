# Starts the whole local stack: databases, backend, agent (simulated driver) and frontend, already paired.
#   pwsh tools/dev-up.ps1            start everything
#   pwsh tools/dev-down.ps1          stop what this script started
# Processes run hidden; logs and PIDs go to .dev/ (git-ignored). The dev agent key is created here and only passed
# to the agent's environment, never written to disk.
param([switch]$NoFrontend, [switch]$NoAgent)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dev = Join-Path $root '.dev'
New-Item -ItemType Directory -Force $dev | Out-Null
$api = 'http://localhost:5044'
$pidFile = Join-Path $dev 'pids.json'
if (Test-Path $pidFile) { throw 'Already running (or not stopped cleanly). Run tools/dev-down.ps1 first.' }
$pids = @{}

function Wait-For($what, $seconds, [scriptblock]$test) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { try { if (& $test) { return } } catch {}; Start-Sleep -Milliseconds 500 }
    throw "Timed out waiting for $what. See logs in $dev"
}

Write-Host '1/5 databases' -ForegroundColor Cyan
docker compose -f (Join-Path $root 'context\auditor-pack\deploy\docker-compose.yml') up -d
if ($LASTEXITCODE -ne 0) { throw 'docker compose failed' }
Wait-For 'PostgreSQL' 60 { (docker exec auditor-pg pg_isready -U postgres) -match 'accepting' }
Wait-For 'ClickHouse' 60 { (Invoke-WebRequest 'http://localhost:8123/ping' -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 }

Write-Host '2/5 backend' -ForegroundColor Cyan
dotnet build (Join-Path $root 'Backend\Auditing\server\server.slnx') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'backend build failed' }
$backend = Start-Process dotnet -ArgumentList 'run', '--no-build', '--launch-profile', 'http', '--project', (Join-Path $root 'Backend\Auditing\server\server\server.csproj') `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dev 'backend.log') -RedirectStandardError (Join-Path $dev 'backend.err')
$pids.backend = $backend.Id
Wait-For 'the backend' 90 { (Invoke-WebRequest "$api/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 }

if (-not $NoAgent) {
    Write-Host '3/5 pair an agent' -ForegroundColor Cyan
    $headers = @{ 'Content-Type' = 'application/json' }
    $name = "local-$($env:COMPUTERNAME)".ToLower()
    $existing = (Invoke-RestMethod "$api/api/agents") | Where-Object { $_.name -eq $name }
    # The key is only shown on creation or rotation, so a leftover local agent gets a fresh key (same agent, same servers).
    $agent = if ($existing) { Invoke-RestMethod -Method Post "$api/api/agents/$($existing.id)/rotate-key" }
             else { Invoke-RestMethod -Method Post "$api/api/agents" -Headers $headers -Body (@{ name = $name } | ConvertTo-Json) }

    $hostName = $env:COMPUTERNAME.ToLower()
    $server = (Invoke-RestMethod "$api/api/servers") | Where-Object { $_.hostName -eq $hostName }
    if (-not $server) {
        $server = Invoke-RestMethod -Method Post "$api/api/servers" -Headers $headers -Body (@{ agentId = $agent.id; hostName = $hostName } | ConvertTo-Json)
    }
    elseif ($server.agentId -ne $agent.id) {
        throw "Server '$hostName' is registered to another agent. Remove it or use a clean database."
    }
    if (-not $server.approved) { Invoke-RestMethod -Method Post "$api/api/servers/$($server.id)/approve" | Out-Null }

    Write-Host '4/5 agent' -ForegroundColor Cyan
    dotnet build (Join-Path $root 'Backend\Collector\Agent\Agent.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'agent build failed' }
    $bin = Join-Path $root 'Backend\Collector\Agent\Agent\bin\Debug\net10.0-windows'
    $env:Agent__BackendUrl = $api
    $env:Agent__ApiKey = $agent.apiKey
    $env:Agent__ServerId = $server.id
    $env:Agent__DataDirectory = Join-Path $dev 'agent-data'
    $env:Agent__SimulatedIntervalMs = '1000'
    $proc = Start-Process (Join-Path $bin 'Auditor.Agent.exe') -WorkingDirectory $bin -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dev 'agent.log') -RedirectStandardError (Join-Path $dev 'agent.err')
    Remove-Item env:Agent__ApiKey, env:Agent__ServerId, env:Agent__BackendUrl, env:Agent__DataDirectory, env:Agent__SimulatedIntervalMs
    $pids.agent = $proc.Id
    Wait-For 'the agent to connect' 30 { (Invoke-RestMethod "$api/api/agents") | Where-Object { $_.id -eq $agent.id -and $_.online } }
}

if (-not $NoFrontend) {
    Write-Host '5/5 frontend' -ForegroundColor Cyan
    $client = Join-Path $root 'Frontend\Lepide\client'
    if (-not (Test-Path (Join-Path $client 'node_modules'))) { Push-Location $client; npm install; Pop-Location }
    $fe = Start-Process 'cmd.exe' -ArgumentList '/c', 'npm run dev' -WorkingDirectory $client -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dev 'frontend.log') -RedirectStandardError (Join-Path $dev 'frontend.err')
    $pids.frontend = $fe.Id
    Wait-For 'the frontend' 90 { (Invoke-WebRequest 'http://localhost:3000' -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 }
}

$pids | ConvertTo-Json | Set-Content $pidFile
Write-Host ''
Write-Host 'Up:' -ForegroundColor Green
Write-Host "  UI       http://localhost:3000/wfsa"
Write-Host "  API      $api/swagger"
Write-Host "  Logs     $dev"
Write-Host '  The agent is paired and uses the simulated driver: start a session on folder D:\Finance (operations Created/Modified/Renamed/Deleted) to see live rows.'
