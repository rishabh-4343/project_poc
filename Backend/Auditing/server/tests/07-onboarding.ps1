# Agent onboarding (register / revoke), share push to the agent, and AgentOffline integrity events.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null; $agent = $null; $ui = $null
try {
    $app = Start-App

    Write-Host '== register an agent'
    $r = Http POST '/api/agents' @{ name = 'fs-agent-1' }
    Assert-Eq 'created' $r.s 201
    Assert-That 'the key is returned' ($r.j.apiKey -like 'ag-*')
    $key = $r.j.apiKey; $agentId = $r.j.id
    Assert-Eq 'only a hash is stored' (P "select count(*) from core.agents where id='$agentId' and api_key_hash is not null") 1
    Assert-Eq 'a duplicate name is refused' (Http POST '/api/agents' @{ name = 'fs-agent-1' }).s 409
    Assert-Eq 'a blank name is refused' (Http POST '/api/agents' @{ name = '  ' }).s 400
    Assert-That 'the list never shows the key' (-not ((Http GET '/api/agents').r.Content -match 'ag-'))

    $srv = (Http POST '/api/servers' @{ agentId = $agentId; hostName = 'fs02' }).j
    Http POST "/api/servers/$($srv.id)/approve" | Out-Null
    Http POST '/api/shares' @{ serverId = $srv.id; name = 'Finance'; localPath = 'D:\Finance'; uncPath = '\\fs02\Finance' } | Out-Null

    Write-Host '== connect: shares and presence'
    $ui = Hub-Connect '/hubs/ui'
    Start-Sleep -Milliseconds 500
    $agent = Hub-Connect '/hubs/agent' $key
    $m = Hub-Next $agent 'UpdateShares'
    Assert-That 'UpdateShares is pushed on connect' ($null -ne $m -and $m.arguments[0].serverId -eq $srv.id)
    Assert-Eq 'with the share name and local path' "$($m.arguments[0].shares[0].name)|$($m.arguments[0].shares[0].localPath)" 'Finance|D:\Finance'
    $up = Hub-Next $ui 'AgentUpdated'
    Assert-That 'AgentUpdated (online) reaches the UI' ($null -ne $up -and $up.arguments[0].online -eq $true -and $up.arguments[0].id -eq $agentId)

    Http POST '/api/shares' @{ serverId = $srv.id; name = 'HR'; localPath = 'D:\HR'; uncPath = '\\fs02\HR' } | Out-Null
    $m2 = Hub-Next $agent 'UpdateShares'
    Assert-Eq 'a new share is pushed live, with the full list' (@($m2.arguments[0].shares) | ForEach-Object { $_.name }) 'Finance HR'

    Write-Host '== AgentOffline'
    $open = "select count(*) from wfsa.integrity_events where server_id='$($srv.id)' and kind='AgentOffline' and ended_at is null"
    Assert-Eq 'nothing open while connected' (P $open) 0
    Hub-Close $agent; $agent = $null
    Start-Sleep -Seconds 2
    Assert-Eq 'a disconnect opens an AgentOffline event' (P $open) 1
    $down = @(Hub-Drain $ui 1000 | Where-Object { $_.target -eq 'AgentUpdated' -and $_.arguments[0].online -eq $false })
    Assert-That 'AgentUpdated (offline) reaches the UI' ($down.Count -ge 1)
    $agent = Hub-Connect '/hubs/agent' $key
    Start-Sleep -Seconds 1
    Assert-Eq 'a reconnect closes it' (P $open) 0
    Assert-Eq 'and it is kept as history' (P "select count(*) from wfsa.integrity_events where server_id='$($srv.id)' and kind='AgentOffline' and ended_at is not null") 1
    Assert-Eq 'the report endpoint lists it' (@((Http GET '/api/wfsa/integrity-events').j | Where-Object { $_.kind -eq 'AgentOffline' }).Count) 1

    Write-Host '== rotate key'
    $rot = Http POST "/api/agents/$agentId/rotate-key"
    Assert-That 'rotation returns a new key for the same agent' ($rot.s -eq 200 -and $rot.j.id -eq $agentId -and $rot.j.apiKey -like 'ag-*' -and $rot.j.apiKey -ne $key)
    $oldRejected = $false
    try { $x = Hub-Connect '/hubs/agent' $key; $null = Hub-Call $x 'EventBatch' @{ serverId = $srv.id; actions = @() } } catch { $oldRejected = $true }
    Assert-That 'the old key stops working' $oldRejected
    Hub-Close $agent; $agent = Hub-Connect '/hubs/agent' $rot.j.apiKey
    Assert-That 'the new key connects' ($null -ne (Hub-Next $agent 'UpdateShares'))
    Assert-Eq 'an unknown agent is a 404 on rotate' (Http POST "/api/agents/$([guid]::NewGuid())/rotate-key").s 404
    $key = $rot.j.apiKey

    Write-Host '== revoke'
    Assert-Eq 'revoke succeeds' (Http POST "/api/agents/$agentId/revoke").s 204
    Assert-Eq 'revoking again is a 404' (Http POST "/api/agents/$agentId/revoke").s 404
    Assert-Eq 'an unknown agent is a 404' (Http POST "/api/agents/$([guid]::NewGuid())/revoke").s 404
    Assert-That 'the revoked agent is no longer listed' (@((Http GET '/api/agents').j | Where-Object { $_.id -eq $agentId }).Count -eq 0)
    $rejected = $false
    try { $x = Hub-Connect '/hubs/agent' $key; $null = Hub-Call $x 'EventBatch' @{ serverId = $srv.id; actions = @() } } catch { $rejected = $true }
    Assert-That 'its key no longer connects' $rejected
    Assert-That 'the name can be used again' ((Http POST '/api/agents' @{ name = 'fs-agent-1' }).s -eq 201)
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally { Hub-Close $agent; Hub-Close $ui; Stop-App $app; Reset-Data }
Finish-Tests '07-onboarding'
