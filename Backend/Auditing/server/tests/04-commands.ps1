# B4: sessions drive the agent: start/stop commands, results, resend on reconnect, active-scope resync, expiry, SessionUpdated.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null; $agent = $null; $ui = $null
try {
    $app = Start-App -EnvVars @{ Wfsa__CommandTtlSeconds = '12'; Wfsa__ExpiryPollSeconds = '1' }
    $fx = New-Fixture; $sid = $fx.ServerId
    function Sess($id) { P "select state||'|'||coalesce(failure_reason,'') from wfsa.sessions where id='$id'" }
    function Cmds($id) { P "select string_agg(type||':'||status, ' ; ' order by issued_at) from core.commands where ref_id='$id'" }
    function New-Session($name) { (Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = $name; operations = @('Created', 'Modified'); folders = @(@{ localPath = "D:\$name" }) }) }
    function Reconnect() { Hub-Close $script:agent; Start-Sleep -Milliseconds 1500; $script:agent = Hub-Connect '/hubs/agent' $script:app.Key }

    $agent = Hub-Connect '/hubs/agent' $app.Key
    $ui = Hub-Connect '/hubs/ui'
    Start-Sleep -Milliseconds 500

    Write-Host '== agent presence'
    Assert-Eq 'agent listed as online while connected' @((Http GET '/api/agents').j)[0].online $true
    Assert-Eq 'last seen is recorded' (P "select last_seen_at is not null from core.agents where name='dev-agent'") 't'
    $r = New-Session 'A'; $A = $r.j
    Assert-Eq 'create returns Starting when the agent is online' $A.state 'Starting'
    $c = Hub-Next $agent 'StartScope'
    Assert-That 'StartScope pushed to the agent' ($null -ne $c)
    $p = $c.arguments[0].payload
    Assert-Eq 'payload session id' $p.sessionId $A.id
    Assert-Eq 'payload operations' (@($p.operations) -join ',') 'Created,Modified'
    Assert-Eq 'payload folder' $p.folders[0].localPath 'D:\A'
    Assert-That 'command has an expiry' ([bool]$c.arguments[0].expiresAt)
    Assert-Eq 'command marked Sent' (Cmds $A.id) 'StartScope:Sent'
    Assert-Eq 'result accepted' (Answer-Command $agent $c) 'ok'
    Assert-Eq 'session is Active' (Sess $A.id) 'Active|'
    Assert-Eq 'a repeated result is accepted but changes nothing' (Answer-Command $agent $c $false 'late') 'ok'
    Assert-Eq 'still Active' (Sess $A.id) 'Active|'
    Assert-That 'an unknown command id is rejected' ((Hub-Call $agent 'CommandResult' @{ commandId = [guid]::NewGuid(); success = $true; error = $null; result = $null }).error -like '*Unknown command*')
    $stop = Http POST "/api/wfsa/sessions/$($A.id)/stop"
    Assert-Eq 'stop returns Stopping' $stop.j.state 'Stopping'
    $sc = Hub-Next $agent 'StopScope'
    Assert-Eq 'StopScope pushed for the same session' $sc.arguments[0].payload.sessionId $A.id
    Assert-Eq 'stop result accepted' (Answer-Command $agent $sc) 'ok'
    Assert-Eq 'session is Completed' (Sess $A.id) 'Completed|'

    Write-Host '== SessionUpdated reaches the tenant group on every transition'
    $updates = @(Hub-Drain $ui 1000 | Where-Object { $_.target -eq 'SessionUpdated' -and $_.arguments[0].id -eq $A.id })
    Assert-Eq 'states pushed for session A' (($updates | ForEach-Object { $_.arguments[0].state }) -join ',') 'Starting,Active,Stopping,Completed'
    Assert-That 'update carries the fields clients merge' ($updates[1].arguments[0].activeAt -and $updates[3].arguments[0].stoppedAt -and $updates[3].arguments[0].serverId -eq $sid)

    Write-Host '== B. agent rejects the scope'
    $B = (New-Session 'B').j; $c = Hub-Next $agent 'StartScope'
    Assert-Eq 'failure result accepted' (Answer-Command $agent $c $false 'Path D:\B not found') 'ok'
    Assert-Eq 'session Failed with the agent reason' (Sess $B.id) 'Failed|Path D:\B not found'
    Assert-Eq 'command Failed' (Cmds $B.id) 'StartScope:Failed'
    $ub = @(Hub-Drain $ui 800 | Where-Object { $_.target -eq 'SessionUpdated' -and $_.arguments[0].id -eq $B.id })
    Assert-Eq 'Failed pushed with its reason' (($ub | Select-Object -Last 1).arguments[0].failureReason) 'Path D:\B not found'

    Write-Host '== C. stop while Starting; a late start result must not reactivate'
    $C = (New-Session 'C').j; $cs = Hub-Next $agent 'StartScope'
    Assert-Eq 'stop returns Stopping' (Http POST "/api/wfsa/sessions/$($C.id)/stop").j.state 'Stopping'
    $cx = Hub-Next $agent 'StopScope'
    Assert-Eq 'late start result accepted' (Answer-Command $agent $cs) 'ok'
    Assert-Eq 'session stays Stopping' (Sess $C.id) 'Stopping|'
    Assert-Eq 'stop result accepted' (Answer-Command $agent $cx) 'ok'
    Assert-Eq 'session Completed' (Sess $C.id) 'Completed|'

    Write-Host '== D. active-scope resync after the agent reconnects'
    $D = (New-Session 'D').j; $c = Hub-Next $agent 'StartScope'; Answer-Command $agent $c | Out-Null
    $Z = (New-Session 'Z').j; $c = Hub-Next $agent 'StartScope'; Answer-Command $agent $c $false 'bad' | Out-Null
    Reconnect
    $got = @(Hub-Drain $agent 1500)
    $starts = @($got | Where-Object { $_.target -eq 'StartScope' })
    Assert-Eq 'only the Active session is resent' (($starts | ForEach-Object { $_.arguments[0].payload.sessionId }) -join ',') $D.id
    Assert-Eq 'a resync adds one command row' (P "select count(*) from core.commands where ref_id='$($D.id)' and type='StartScope'") 2
    Assert-Eq 'Completed and Failed sessions untouched' "$(Sess $Z.id)/$(Cmds $Z.id)" 'Failed|bad/StartScope:Failed'
    $firstId = $starts[0].arguments[0].commandId
    Reconnect
    $again = @(Hub-Drain $agent 1500 | Where-Object { $_.target -eq 'StartScope' })
    Assert-Eq 'an unanswered resync is resent with the same command id' $again[0].arguments[0].commandId $firstId
    Assert-Eq 'no extra command rows were created' (P "select count(*) from core.commands where ref_id='$($D.id)' and type='StartScope'") 2
    Assert-Eq 'resync failure degrades the session' (Answer-Command $agent $again[0] $false 'driver port unavailable') 'ok'
    Assert-Eq 'Degraded with the reason' (Sess $D.id) 'Degraded|driver port unavailable'
    Reconnect
    $rec = @(Hub-Drain $agent 1500 | Where-Object { $_.target -eq 'StartScope' })
    Assert-Eq 'a Degraded session is resent' $rec.Count 1
    Assert-Eq 'recovery answered' (Answer-Command $agent $rec[0]) 'ok'
    Assert-Eq 'back to Active with the reason cleared' (Sess $D.id) 'Active|'
    Assert-Eq 'a Degraded or Active session can be stopped' (Http POST "/api/wfsa/sessions/$($D.id)/stop").j.state 'Stopping'
    $sd = Hub-Next $agent 'StopScope'; Answer-Command $agent $sd | Out-Null
    Assert-Eq 'completed with no failure reason' (Sess $D.id) 'Completed|'

    Write-Host '== E. offline create is delivered on reconnect'
    Hub-Close $agent; $agent = $null; Start-Sleep -Milliseconds 1500
    $E = (New-Session 'E').j
    Assert-Eq 'Requested while the agent is offline' $E.state 'Requested'
    Assert-Eq 'agent listed as offline after disconnecting' @((Http GET '/api/agents').j)[0].online $false
    Assert-Eq 'status column follows' (P "select status from core.agents where name='dev-agent'") 'offline'
    Assert-Eq 'command Pending' (Cmds $E.id) 'StartScope:Pending'
    $agent = Hub-Connect '/hubs/agent' $app.Key
    $c = Hub-Next $agent 'StartScope'
    Assert-Eq 'resent on connect' $c.arguments[0].payload.sessionId $E.id
    Assert-Eq 'now Starting' (Sess $E.id) 'Starting|'
    Answer-Command $agent $c | Out-Null
    Assert-Eq 'then Active' (Sess $E.id) 'Active|'
    $sx = (Http POST "/api/wfsa/sessions/$($E.id)/stop"); $sc = Hub-Next $agent 'StopScope'; Answer-Command $agent $sc | Out-Null

    Write-Host '== F. stopping an undelivered session cancels its command'
    Hub-Close $agent; $agent = $null; Start-Sleep -Milliseconds 1500
    $F = (New-Session 'F').j
    Assert-Eq 'stop completes it' (Http POST "/api/wfsa/sessions/$($F.id)/stop").j.state 'Completed'
    Assert-Eq 'command cancelled' (Cmds $F.id) 'StartScope:Failed'
    $agent = Hub-Connect '/hubs/agent' $app.Key
    Assert-That 'nothing is resent for it' ($null -eq (Hub-Next $agent 'StartScope' 2500))

    Write-Host '== G. an undelivered command expires'
    Hub-Close $agent; $agent = $null; Start-Sleep -Milliseconds 1500
    $G = (New-Session 'G').j
    Start-Sleep -Seconds 15
    Assert-Eq 'session Failed' (Sess $G.id) 'Failed|Start command expired.'
    Assert-Eq 'command Expired' (Cmds $G.id) 'StartScope:Expired'

    Write-Host '== H. a delivered but unanswered command expires, and a late result is ignored'
    $agent = Hub-Connect '/hubs/agent' $app.Key
    $H = (New-Session 'H').j; $c = Hub-Next $agent 'StartScope'
    Start-Sleep -Seconds 15
    Assert-Eq 'session Failed' (Sess $H.id) 'Failed|Start command expired.'
    Assert-Eq 'a late success is accepted but changes nothing' (Answer-Command $agent $c) 'ok'
    Assert-Eq 'still Failed' (Sess $H.id) 'Failed|Start command expired.'

    Write-Host '== I. an unanswered resync degrades the session on expiry'
    $I = (New-Session 'I').j; $c = Hub-Next $agent 'StartScope'; Answer-Command $agent $c | Out-Null
    Hub-Close $agent; Start-Sleep -Milliseconds 1500
    $agent = Hub-Connect '/hubs/agent' $app.Key
    $null = Hub-Next $agent 'StartScope'
    Start-Sleep -Seconds 15
    Assert-Eq 'Degraded: scope resync expired' (Sess $I.id) 'Degraded|Scope resync expired.'
    Assert-Eq 'no exceptions in the log other than the unknown-command rejection' (Get-LogLines $app 'Exception:').Count 1
}
catch { Assert-That 'script ran without an unexpected error' $false "$($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)" }
finally { Hub-Close $agent; Hub-Close $ui; Stop-App $app; Reset-Data }
Finish-Tests '04-commands'
