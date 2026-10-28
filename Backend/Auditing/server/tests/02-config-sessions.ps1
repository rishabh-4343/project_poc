# B2: servers, shares and session CRUD, including validation and conflict cases (no agent connected).
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null
try {
    $app = Start-App
    $agent = P "select id from core.agents where name='dev-agent'"
    $folders = @(@{ localPath = 'D:\Finance' })

    Write-Host '== agents'
    $ag = @((Http GET '/api/agents').j)
    Assert-Eq 'one agent listed' $ag.Count 1
    Assert-Eq 'agent name and offline status' "$($ag[0].name)/$($ag[0].online)" 'dev-agent/False'
    Assert-That 'key hash is never exposed' (-not ((Http GET '/api/agents').r.Content -match 'hash|apiKey'))

    Write-Host '== servers'
    Assert-Eq 'unknown agent' (Http POST '/api/servers' @{ agentId = '11111111-1111-1111-1111-111111111111'; hostName = 'fs01' }).s 400
    Assert-Eq 'blank host name' (Http POST '/api/servers' @{ agentId = $agent; hostName = ' ' }).s 400
    $s = Http POST '/api/servers' @{ agentId = $agent; hostName = 'fs01'; osVersion = 'Windows Server 2022' }
    Assert-Eq 'register' $s.s 201
    $sid = $s.j.id
    Assert-Eq 'duplicate host' (Http POST '/api/servers' @{ agentId = $agent; hostName = 'fs01' }).s 409
    $list = (Http GET '/api/servers').j
    Assert-Eq 'list count' @($list).Count 1
    Assert-Eq 'new server is not approved' $list[0].approved $false
    Assert-Eq 'approve unknown' (Http POST "/api/servers/$([guid]::NewGuid())/approve").s 404

    Write-Host '== session on an unapproved server'
    Assert-Eq 'unapproved server' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @('Created'); folders = $folders }).s 409
    Assert-Eq 'approve' (Http POST "/api/servers/$sid/approve").s 200

    Write-Host '== shares'
    Assert-Eq 'share on unknown server' (Http POST '/api/shares' @{ serverId = [guid]::NewGuid(); name = 'Finance'; localPath = 'D:\Finance'; uncPath = '\\fs01\Finance' }).s 400
    Assert-Eq 'share with a missing field' (Http POST '/api/shares' @{ serverId = $sid; name = 'Finance' }).s 400
    $sh = Http POST '/api/shares' @{ serverId = $sid; name = 'Finance'; localPath = 'D:\Finance'; uncPath = '\\fs01\Finance' }
    Assert-Eq 'register share' $sh.s 201
    Assert-Eq 'duplicate share path' (Http POST '/api/shares' @{ serverId = $sid; name = 'F2'; localPath = 'D:\Finance'; uncPath = '\\fs01\F2' }).s 409
    Assert-Eq 'list shares by server' @((Http GET "/api/shares?serverId=$sid").j).Count 1

    Write-Host '== session validation'
    Assert-Eq 'no operations' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @(); folders = $folders }).s 400
    Assert-Eq 'no folders' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @('Created'); folders = @() }).s 400
    Assert-Eq 'unknown operation' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @('Nope'); folders = $folders }).s 400
    Assert-Eq 'share from another server' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @('Created'); folders = @(@{ localPath = 'D:\Finance'; shareId = [guid]::NewGuid() }) }).s 400
    Assert-Eq 'duplicate folders (case-insensitive)' (Http POST '/api/wfsa/sessions' @{ serverId = $sid; operations = @('Created'); folders = @(@{ localPath = 'D:\A' }, @{ localPath = 'd:\a' }) }).s 400

    Write-Host '== session create, read, list, stop'
    $c = Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = 'Finance watch'; operations = @('Created', 'Modified', 'Deleted', 'Renamed', 'Created'); includeReads = $true; folders = @(@{ localPath = 'D:\Finance\Confidential'; shareId = $sh.j.id }) }
    Assert-Eq 'create' $c.s 201
    $id = $c.j.id
    Assert-Eq 'state is Requested while the agent is offline' $c.j.state 'Requested'
    Assert-Eq 'operations are de-duplicated' (@($c.j.operations) -join ',') 'Created,Modified,Deleted,Renamed'
    Assert-Eq 'get' (Http GET "/api/wfsa/sessions/$id").j.folders[0].localPath 'D:\Finance\Confidential'
    Assert-Eq 'get unknown' (Http GET "/api/wfsa/sessions/$([guid]::NewGuid())").s 404
    Assert-Eq 'list by state' @((Http GET '/api/wfsa/sessions?state=Requested').j).Count 1
    Assert-Eq 'list by another state' @((Http GET '/api/wfsa/sessions?state=Active').j).Count 0
    Assert-Eq 'list with a bad state' (Http GET '/api/wfsa/sessions?state=Bogus').s 400
    Assert-Eq 'row in PostgreSQL' (P "select state||' '||operations::text||' '||include_reads from wfsa.sessions where id='$id'") 'Requested {Created,Modified,Deleted,Renamed} true'
    Assert-Eq 'folder linked to its share' (P "select count(*) from wfsa.session_folders where session_id='$id' and share_id is not null") 1
    Assert-Eq 'a pending StartScope command exists' (P "select type||':'||status from core.commands where ref_id='$id'") 'StartScope:Pending'

    $stop = Http POST "/api/wfsa/sessions/$id/stop"
    Assert-Eq 'stop an undelivered session' $stop.s 200
    Assert-Eq 'it completes at once' $stop.j.state 'Completed'
    Assert-Eq 'its pending command is cancelled' (P "select status from core.commands where ref_id='$id'") 'Failed'
    Assert-Eq 'stop again' (Http POST "/api/wfsa/sessions/$id/stop").s 409
    Assert-Eq 'stop unknown' (Http POST "/api/wfsa/sessions/$([guid]::NewGuid())/stop").s 404
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally { Stop-App $app; Reset-Data }
Finish-Tests '02-config-sessions'
