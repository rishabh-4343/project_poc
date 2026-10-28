# Agent health: Heartbeat, IntegrityEvent, the integrity-event reports, AgentUpdated push, and MaxEndAt enforcement.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null; $agent = $null; $ui = $null
try {
    $app = Start-App -EnvVars @{ Wfsa__ExpiryPollSeconds = '1' }
    $fx = New-Fixture; $sid = $fx.ServerId
    $agent = Hub-Connect '/hubs/agent' $app.Key
    $ui = Hub-Connect '/hubs/ui'
    Start-Sleep -Milliseconds 500
    $t0 = [DateTimeOffset]::UtcNow
    function With($h, $o) { $c = $h.Clone(); foreach ($k in $o.Keys) { $c[$k] = $o[$k] }; $c }

    Write-Host '== heartbeat'
    $r = Hub-Call $agent 'Heartbeat' @{ agentVersion = '1.2.3'; driverVersion = '0.9.0'; servers = @(@{ serverId = $sid; droppedCount = 5; ringLag = 2; outboxSize = 10; activeScopes = 1 }) }
    Assert-That 'heartbeat accepted' (-not $r.error) ($r.error)
    Assert-Eq 'versions stored on the agent' (P "select agent_version||'|'||driver_version from core.agents where name='dev-agent'") '1.2.3|0.9.0'
    Assert-Eq 'dropped count stored' (P "select dropped_count from wfsa.agent_positions where server_id='$sid'") 5
    $push = $null
    for ($i = 0; $i -lt 5 -and -not ($push -and $push.arguments[0].agentVersion); $i++) { $push = Hub-Next $ui 'AgentUpdated' }   # the first push is the presence change on connect
    Assert-That 'AgentUpdated pushed to the tenant group' ($null -ne $push -and $push.arguments[0].agentVersion -eq '1.2.3')
    Assert-Eq 'agent list shows the version' @((Http GET '/api/agents').j)[0].agentVersion '1.2.3'
    Hub-Call $agent 'Heartbeat' @{ agentVersion = '1.2.3'; driverVersion = '0.9.0'; servers = @(@{ serverId = $sid; droppedCount = 0; ringLag = 0; outboxSize = 0; activeScopes = 0 }) } | Out-Null
    Assert-Eq 'a driver restart resets the dropped count' (P "select dropped_count from wfsa.agent_positions where server_id='$sid'") 0
    Assert-That 'heartbeat for an unknown server is rejected' ((Hub-Call $agent 'Heartbeat' @{ servers = @(@{ serverId = [guid]::NewGuid(); droppedCount = 1 }) }).error -like '*Unknown server*')
    Assert-That 'heartbeat with no servers is fine' (-not (Hub-Call $agent 'Heartbeat' @{ agentVersion = '1.2.3' }).error)

    Write-Host '== integrity events'
    $ev = @{ serverId = $sid; kind = 'DriverOverflow'; startedAt = $t0.AddMinutes(-1).ToString('o'); endedAt = $null; firstSeq = 10; lastSeq = $null; droppedCount = 40; details = 'ring full' }
    Assert-That 'event accepted' (-not (Hub-Call $agent 'IntegrityEvent' $ev).error)
    Assert-Eq 'one row' (P "select count(*) from wfsa.integrity_events where server_id='$sid'") 1
    Assert-Eq 'overflow raises the dropped count' (P "select dropped_count from wfsa.agent_positions where server_id='$sid'") 40
    Hub-Call $agent 'IntegrityEvent' $ev | Out-Null
    Assert-Eq 'a resend adds no row' (P "select count(*) from wfsa.integrity_events where server_id='$sid'") 1
    $ev.endedAt = $t0.ToString('o'); $ev.lastSeq = 55; $ev.droppedCount = 42
    Hub-Call $agent 'IntegrityEvent' $ev | Out-Null
    Assert-Eq 'the same event is closed in place' (P "select count(*)||'|'||(ended_at is not null)||'|'||last_seq from wfsa.integrity_events where server_id='$sid' group by ended_at, last_seq") '1|true|55'
    Assert-Eq 'details keep the message' (P "select details->>'message' from wfsa.integrity_events where server_id='$sid'") 'ring full'
    Assert-That 'unknown kind is rejected' ((Hub-Call $agent 'IntegrityEvent' (With $ev @{ kind = 'Nope' })).error)
    Assert-That 'end before start is rejected' ((Hub-Call $agent 'IntegrityEvent' (With $ev @{ startedAt = $t0.AddMinutes(5).ToString('o') })).error -like '*before*')
    Assert-That 'unknown server is rejected' ((Hub-Call $agent 'IntegrityEvent' (With $ev @{ serverId = [guid]::NewGuid() })).error -like '*Unknown server*')

    Write-Host '== reports'
    $recent = Http GET '/api/wfsa/integrity-events?limit=5'
    Assert-Eq 'recent events' @($recent.j).Count 1
    Assert-Eq 'recent event fields' "$($recent.j[0].kind)|$($recent.j[0].serverId)" "DriverOverflow|$sid"
    Hub-Call $agent 'IntegrityEvent' @{ serverId = $sid; kind = 'Gap'; startedAt = $t0.AddMinutes(-2).ToString('o'); firstSeq = 100 } | Out-Null
    $S = (Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = 'H'; operations = @('Created'); folders = @(@{ localPath = 'D:\H' }) }).j
    $c = Hub-Next $agent 'StartScope'
    Answer-Command $agent $c | Out-Null
    $sev = @((Http GET "/api/wfsa/sessions/$($S.id)/integrity-events").j)
    Assert-Eq 'a session lists only events overlapping its window (open Gap yes, closed overflow no)' (($sev | ForEach-Object { $_.kind }) -join ',') 'Gap'
    Assert-Eq 'unknown session' (Http GET "/api/wfsa/sessions/$([guid]::NewGuid())/integrity-events").s 404
    Http POST "/api/wfsa/sessions/$($S.id)/stop" | Out-Null
    Answer-Command $agent (Hub-Next $agent 'StopScope') | Out-Null

    Write-Host '== MaxEndAt'
    $M = (Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = 'M'; operations = @('Created'); folders = @(@{ localPath = 'D:\M' }); maxEndAt = [DateTimeOffset]::UtcNow.AddSeconds(4).ToString('o') }).j
    Answer-Command $agent (Hub-Next $agent 'StartScope') | Out-Null
    Assert-Eq 'session is Active before its end time' (P "select state from wfsa.sessions where id='$($M.id)'") 'Active'
    $sc = Hub-Next $agent 'StopScope' 12000
    Assert-That 'StopScope is issued once MaxEndAt passes' ($null -ne $sc -and $sc.arguments[0].payload.sessionId -eq $M.id)
    Assert-Eq 'session is Stopping' (P "select state from wfsa.sessions where id='$($M.id)'") 'Stopping'
    Assert-Eq 'stopped_by is empty for a system stop' (P "select coalesce(stopped_by::text,'none') from wfsa.sessions where id='$($M.id)'") 'none'
    Answer-Command $agent $sc | Out-Null
    Assert-Eq 'session is Completed' (P "select state from wfsa.sessions where id='$($M.id)'") 'Completed'
    Start-Sleep -Seconds 3
    Assert-Eq 'no second stop command is issued' (P "select count(*) from core.commands where ref_id='$($M.id)' and type='StopScope'") 1
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally { Hub-Close $agent; Hub-Close $ui; Stop-App $app; Reset-Data }
Finish-Tests '06-agent-health'
