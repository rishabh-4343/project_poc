# Shared helpers for the backend test scripts. Dot-source it from a test script; run them with run-all.ps1.
if ($env:AUDITOR_TESTS_OK -ne '1') {
    throw 'These tests DELETE all servers, sessions, commands and agents and truncate the wfsa ClickHouse tables in the local dev databases. Set AUDITOR_TESTS_OK=1 to continue (run-all.ps1 -Yes does this).'
}

$script:Project = (Resolve-Path (Join-Path $PSScriptRoot '..\server\server.csproj')).Path
$script:Base = 'http://127.0.0.1:5058'
$script:DevTenant = '00000000-0000-0000-0000-000000000001'
$script:DevUser = '00000000-0000-0000-0000-000000000002'
$script:Passes = 0
$script:Failures = 0
$RS = [char]0x1e

# ---- assertions -------------------------------------------------------------------------------
function Assert-That($name, $condition, $detail = '') {
    if ($condition) { $script:Passes++; Write-Host "  PASS  $name" -ForegroundColor DarkGreen }
    else { $script:Failures++; Write-Host "  FAIL  $name $detail" -ForegroundColor Red }
}

function Assert-Eq($name, $actual, $expected) {
    Assert-That $name ("$actual" -eq "$expected") "(expected '$expected', got '$actual')"
}

function Finish-Tests($title) {
    Write-Host "$title : $script:Passes passed, $script:Failures failed" -ForegroundColor $(if ($script:Failures) { 'Red' } else { 'Green' })
    exit $(if ($script:Failures) { 1 } else { 0 })
}

# ---- databases --------------------------------------------------------------------------------
function P($sql) { ((docker exec auditor-pg psql -U postgres -d postgres -tAc $sql) -join ' ').Trim() }
function Pg-Exec($sql) { docker exec auditor-pg psql -U postgres -d postgres -q -c $sql 2>&1 | Out-Null }
function Q($sql) { ((docker exec auditor-ch clickhouse-client --password devpassword -q $sql) -join ' ').Trim() }

function Reset-Data {
    foreach ($t in 'actions', 'read_actions', 'daily_action_counts') { docker exec auditor-ch clickhouse-client --password devpassword -q "truncate table if exists wfsa.$t" 2>&1 | Out-Null }
    Pg-Exec "delete from core.commands; delete from wfsa.integrity_events; delete from wfsa.read_batches; delete from wfsa.agent_positions; delete from wfsa.session_folders; delete from wfsa.sessions; delete from ddc.file_classification; delete from core.shares; delete from core.servers; delete from core.agents; delete from core.users where id <> '$script:DevUser'; delete from core.tenants where id <> '$script:DevTenant'"
}

# ---- app lifecycle ----------------------------------------------------------------------------
function Start-App {
    param([hashtable]$EnvVars = @{}, [switch]$KeepAgent, [switch]$AllowUnhealthy, [switch]$NoLaunchProfile)
    Get-Process server -ErrorAction SilentlyContinue | Stop-Process -Force
    if (-not $KeepAgent) { Pg-Exec "delete from core.agents where name='dev-agent'" }

    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $out = Join-Path $env:TEMP "auditor-test-$tag.out"
    $err = Join-Path $env:TEMP "auditor-test-$tag.err"

    foreach ($k in $EnvVars.Keys) { Set-Item "env:$k" $EnvVars[$k] }
    $runArgs = @('run', '--no-build', '--project', $script:Project, '--urls', $script:Base)
    if ($NoLaunchProfile) { $runArgs += '--no-launch-profile' }
    $proc = Start-Process dotnet -ArgumentList $runArgs `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
    foreach ($k in $EnvVars.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }

    $healthy = $false
    for ($i = 0; $i -lt 80; $i++) {
        try { Invoke-WebRequest "$script:Base/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; $healthy = $true; break }
        catch { if ($proc.HasExited) { break }; Start-Sleep -Milliseconds 500 }
    }

    $app = @{ Process = $proc; Out = $out; Err = $err; Healthy = $healthy; Key = $null }
    $line = Select-String -Path $out -Pattern 'DEV AGENT KEY' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($line) { $app.Key = ($line.Line -split ': ')[1].Trim() }

    if (-not $healthy -and -not $AllowUnhealthy) {
        Get-Content $out, $err -ErrorAction SilentlyContinue | Select-Object -First 20
        Stop-App $app
        throw 'app did not become healthy'
    }

    $app
}

function Stop-App($app) {
    if (-not $app) { return }
    Stop-Process -Id $app.Process.Id -Force -ErrorAction SilentlyContinue
    Get-Process server -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $app.Out, $app.Err -ErrorAction SilentlyContinue
}

function Get-LogLines($app, $pattern) {
    @(Get-Content $app.Out, $app.Err -ErrorAction SilentlyContinue | Select-String -Pattern $pattern | ForEach-Object { $_.Line.Trim() })
}

# ---- HTTP -------------------------------------------------------------------------------------
function Http($method, $path, $body = $null, $headers = @{}) {
    $a = @{ Method = $method; Uri = "$script:Base$path"; UseBasicParsing = $true; SkipHttpErrorCheck = $true; ContentType = 'application/json'; Headers = $headers }
    if ($body) { $a.Body = ($body | ConvertTo-Json -Depth 8) }
    $r = Invoke-WebRequest @a
    $j = $null
    try { $j = $r.Content | ConvertFrom-Json } catch {}
    @{ s = [int]$r.StatusCode; j = $j; r = $r }
}

function Seqs($rows) { (@($rows) | ForEach-Object { $_.seq }) -join ',' }

function New-Fixture($host_ = 'fs01') {
    $agent = P "select id from core.agents where name='dev-agent'"
    $srv = (Http POST '/api/servers' @{ agentId = $agent; hostName = $host_ }).j
    Http POST "/api/servers/$($srv.id)/approve" | Out-Null
    @{ AgentId = $agent; ServerId = $srv.id }
}

# ---- SignalR (raw JSON protocol over a WebSocket, so no client package is needed) --------------
function Hub-Connect($path, $key = $null) {
    $w = [System.Net.WebSockets.ClientWebSocket]::new()
    if ($key) { $w.Options.SetRequestHeader('X-Agent-Key', $key) }
    $w.ConnectAsync([uri]"ws://127.0.0.1:5058$path", [Threading.CancellationToken]::None).Wait()
    $o = @{ ws = $w; buf = ''; rt = $null; rb = $null; n = 0; inbox = [System.Collections.ArrayList]::new() }
    Hub-Send $o '{"protocol":"json","version":1}'
    $o
}

function Hub-Send($o, $text) {
    $b = [Text.Encoding]::UTF8.GetBytes($text + $RS)
    $o.ws.SendAsync([ArraySegment[byte]]::new($b), 'Text', $true, [Threading.CancellationToken]::None).Wait()
}

function Hub-Recv($o, $ms) {
    while ($true) {
        $i = $o.buf.IndexOf($RS)
        if ($i -ge 0) {
            $m = $o.buf.Substring(0, $i); $o.buf = $o.buf.Substring($i + 1)
            if ($m -eq '{}' -or $m -match '^\{"type":6\}$') { continue }
            return $m
        }
        if ($null -eq $o.rt) { $o.rb = New-Object byte[] 262144; $o.rt = $o.ws.ReceiveAsync([ArraySegment[byte]]::new($o.rb), [Threading.CancellationToken]::None) }
        if (-not $o.rt.Wait($ms)) { return $null }
        $r = $o.rt.Result; $o.rt = $null
        if ($r.MessageType -eq 'Close') { return $null }
        $o.buf += [Text.Encoding]::UTF8.GetString($o.rb, 0, $r.Count)
    }
}

# Invokes a hub method; returns the result, or @{ error = ... }. Server pushes seen meanwhile go to the inbox.
function Hub-Call($o, $target, $arg) {
    $o.n++; $id = "i$($o.n)"
    Hub-Send $o (@{ type = 1; invocationId = $id; target = $target; arguments = @($arg) } | ConvertTo-Json -Depth 10 -Compress)
    while ($true) {
        $m = Hub-Recv $o 8000
        if ($null -eq $m) { return @{ error = 'TIMEOUT' } }
        $j = $m | ConvertFrom-Json
        if ($j.type -eq 3 -and $j.invocationId -eq $id) { if ($j.error) { return @{ error = $j.error } } else { return $j.result } }
        elseif ($j.type -eq 1) { [void]$o.inbox.Add($j) }
    }
}

# Next pushed invocation for $target (inbox first, then the socket); $null if none arrives within $ms.
function Hub-Next($o, $target, $ms = 3000) {
    for ($k = 0; $k -lt $o.inbox.Count; $k++) {
        if ($o.inbox[$k].target -eq $target) { $m = $o.inbox[$k]; $o.inbox.RemoveAt($k); return $m }
    }
    while ($true) {
        $raw = Hub-Recv $o $ms
        if ($null -eq $raw) { return $null }
        $j = $raw | ConvertFrom-Json
        if ($j.type -ne 1) { continue }
        if ($j.target -eq $target) { return $j }
        [void]$o.inbox.Add($j)
    }
}

# Everything pushed so far plus whatever arrives within $ms of quiet; clears the inbox. Wrap calls in @() when assigning.
function Hub-Drain($o, $ms = 1500) {
    while ($true) {
        $raw = Hub-Recv $o $ms
        if ($null -eq $raw) { break }
        $j = $raw | ConvertFrom-Json
        if ($j.type -eq 1) { [void]$o.inbox.Add($j) }
    }
    $list = @($o.inbox); $o.inbox.Clear()
    $list
}

function Hub-Close($o) { if ($o) { try { $o.ws.Abort(); $o.ws.Dispose() } catch {} } }

function Answer-Command($agent, $command, $success = $true, $error_ = $null) {
    $r = Hub-Call $agent 'CommandResult' @{ commandId = $command.arguments[0].commandId; success = $success; error = $error_; result = $null }
    if ($r.error) { 'ERR:' + $r.error } else { 'ok' }
}

function New-Action($serverId, $seq, $op, $path, $when, $ok = $true, $actor = 'CORP\alice', $proc = 'explorer.exe', $type = 'File', $hash = $null) {
    @{
        tenantId = $script:DevTenant; serverId = $serverId; seq = $seq; occurredAt = $when.ToString('o'); objectType = $type; operation = $op
        success = $ok; certainty = 'Certain'; path = $path; pathHash = $(if ($null -ne $hash) { $hash } else { $seq }); newPath = $null
        shareName = 'Finance'; actorSid = "S-1-5-21-$seq"; actorName = $actor; clientIp = '10.0.0.5'; processName = $proc
        oldSecurity = $null; newSecurity = $null; details = $null
    }
}
