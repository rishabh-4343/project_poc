# B5: report scope, keyset paging, filters, CSV export, dashboard, live push, tenant isolation.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null; $agent = $null; $ui = $null
try {
    $app = Start-App
    $fx = New-Fixture; $sid = $fx.ServerId
    $agent = Hub-Connect '/hubs/agent' $app.Key
    Start-Sleep -Milliseconds 400

    function Start-Session($name, $recursive) {
        $s = (Http POST '/api/wfsa/sessions' @{ serverId = $sid; name = $name; operations = @('Created', 'Modified'); folders = @(@{ localPath = 'D:\Finance'; recursive = $recursive }) }).j
        $c = Hub-Next $agent 'StartScope'
        Answer-Command $agent $c | Out-Null
        $s
    }
    $S = Start-Session 'S' $true
    $T = Start-Session 'T' $false
    Assert-Eq 'both sessions are Active' (P "select string_agg(state::text, ',') from wfsa.sessions") 'Active,Active'

    # A second tenant with its own Active session, to prove isolation.
    Pg-Exec "insert into core.tenants(id,name,slug) values ('99999999-0000-0000-0000-000000000001','other','other'); insert into core.users(id,tenant_id,email,display_name,role) values ('99999999-0000-0000-0000-000000000002','99999999-0000-0000-0000-000000000001','o@o','o','admin'); insert into core.agents(id,tenant_id,name,api_key_hash) values ('99999999-0000-0000-0000-000000000003','99999999-0000-0000-0000-000000000001','other-agent',sha256(convert_to('other','UTF8'))); insert into core.servers(id,tenant_id,agent_id,host_name,approved) values ('99999999-0000-0000-0000-000000000004','99999999-0000-0000-0000-000000000001','99999999-0000-0000-0000-000000000003','osrv',true); insert into wfsa.sessions(id,tenant_id,server_id,agent_id,operations,state,started_by) values ('99999999-0000-0000-0000-000000000005','99999999-0000-0000-0000-000000000001','99999999-0000-0000-0000-000000000004','99999999-0000-0000-0000-000000000003',array['Created']::wfsa.operation[],'Active','99999999-0000-0000-0000-000000000002')"
    $OTHER = '99999999-0000-0000-0000-000000000005'

    Write-Host '== live push'
    $ui = Hub-Connect '/hubs/ui'
    Assert-Eq 'join S' (Hub-Call $ui 'JoinSession' $S.id).error $null
    Assert-Eq 'join T' (Hub-Call $ui 'JoinSession' $T.id).error $null
    Assert-That 'joining an unknown session is rejected' ((Hub-Call $ui 'JoinSession' ([guid]::NewGuid().ToString())).error -like '*Unknown session*')
    Assert-That 'joining another tenant session is rejected' ((Hub-Call $ui 'JoinSession' $OTHER).error -like '*Unknown session*')
    $null = Hub-Drain $ui 500

    $t0 = [DateTimeOffset]::UtcNow.AddSeconds(2)
    function Ev($seq, $op, $path, $ts, $ok = $true, $actor = 'CORP\alice', $proc = 'explorer.exe', $type = 'File') { New-Action $sid $seq $op $path $ts $ok $actor $proc $type }
    $evs = @(
        (Ev 1 'Created' 'D:\Finance\a.xlsx' $t0),
        (Ev 2 'Modified' 'D:\Finance\sub\b.xlsx' $t0 $true 'CORP\alice' 'excel.exe'),
        (Ev 3 'Deleted' 'D:\Finance\c.xlsx' $t0),
        (Ev 4 'Created' 'D:\Other\x.txt' $t0),
        (Ev 5 'Created' 'D:\FinanceX\y.txt' $t0),
        (Ev 6 'Created' 'd:\finance\UPPER.txt' $t0),
        (Ev 7 'Created' 'D:\Finance\denied.txt' $t0 $false),
        (Ev 8 'Created' 'D:\Finance\a,"b".txt' $t0 $true '=SUM(1)' '@evil'),
        (Ev 9 'Created' 'D:\Finance' $t0 $true 'CORP\alice' 'explorer.exe' 'Folder'),
        (Ev 10 'Created' 'D:\Finance\old.txt' $t0.AddHours(-1)),
        (Ev 11 'Modified' 'D:\Finance\late.txt' $t0.AddSeconds(5) $true 'CORP\alice' 'excel.exe'))
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = $evs }
    Assert-Eq 'ingest ack' $ack.stored 11
    $msgs = @(Hub-Drain $ui 2000)
    function Pushed($id) { (@($msgs | Where-Object { $_.target -eq 'ActionsAppended' -and $_.arguments[0] -eq $id } | ForEach-Object { $_.arguments[1] } | ForEach-Object { [int]$_.seq } | Sort-Object) -join ',') }
    Assert-Eq 'pushed to S (recursive)' (Pushed $S.id) '1,2,6,7,8,9,11'
    Assert-Eq 'pushed to T (non-recursive)' (Pushed $T.id) '1,6,7,8,9,11'
    $first = @($msgs | Where-Object { $_.target -eq 'ActionsAppended' })[0].arguments[1][0]
    Assert-That 'a pushed row carries the report fields' ($first.operation -and $first.path -and $first.occurredAt -and $null -ne $first.riskLevel)
    $null = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = $evs }
    Assert-Eq 'a resend pushes nothing' @(Hub-Drain $ui 1200 | Where-Object { $_.target -eq 'ActionsAppended' }).Count 0

    Write-Host '== report scope and order'
    $r = Http GET "/api/wfsa/sessions/$($S.id)/actions"
    Assert-Eq 'S rows, newest first' (Seqs $r.j.items) '11,9,8,7,6,2,1'
    Assert-Eq 'no next cursor on the last page' $r.j.nextCursor $null
    Assert-Eq 'T rows (non-recursive)' (Seqs (Http GET "/api/wfsa/sessions/$($T.id)/actions").j.items) '11,9,8,7,6,1'
    $row = (Http GET "/api/wfsa/sessions/$($S.id)/actions?pathPrefix=D:\Finance\sub").j.items[0]
    Assert-Eq 'row fields' "$($row.objectType)|$($row.operation)|$($row.success)|$($row.actorName)|$($row.clientIp)|$($row.processName)|$($row.riskLevel)" 'File|Modified|True|CORP\alice|10.0.0.5|excel.exe|-1'

    Write-Host '== keyset paging over tied timestamps'
    $all = @(); $cur = $null; $pages = 0
    do {
        $u = "/api/wfsa/sessions/$($S.id)/actions?limit=2"; if ($cur) { $u += "&before=$cur" }
        $pg = (Http GET $u).j; $pages++; $all += @($pg.items); $cur = $pg.nextCursor
    } while ($cur -and $pages -lt 10)
    Assert-Eq 'pages' $pages 4
    Assert-Eq 'every row exactly once, in order' (Seqs $all) '11,9,8,7,6,2,1'
    Assert-Eq 'limit=1' (Seqs (Http GET "/api/wfsa/sessions/$($S.id)/actions?limit=1").j.items) '11'

    Write-Host '== filters'
    $f = "/api/wfsa/sessions/$($S.id)/actions"
    Assert-Eq 'operation=Modified' (Seqs (Http GET "${f}?operation=Modified").j.items) '11,2'
    Assert-Eq 'operation=Modified,Created' (Seqs (Http GET "${f}?operation=Modified,Created").j.items) '11,9,8,7,6,2,1'
    Assert-Eq 'an operation outside the session returns nothing' @((Http GET "${f}?operation=Deleted").j.items).Count 0
    Assert-Eq 'actor=alice' (Seqs (Http GET "${f}?actor=alice").j.items) '11,9,7,6,2,1'
    Assert-Eq 'success=false' (Seqs (Http GET "${f}?success=false").j.items) '7'
    Assert-Eq 'pathPrefix' (Seqs (Http GET "${f}?pathPrefix=D:\Finance\sub").j.items) '2'

    Write-Host '== errors and tenant isolation'
    Assert-Eq 'bad operation' (Http GET "${f}?operation=Bogus").s 400
    Assert-Eq 'bad cursor' (Http GET "${f}?before=zzz").s 400
    Assert-Eq 'unknown session' (Http GET "/api/wfsa/sessions/$([guid]::NewGuid())/actions").s 404
    Assert-Eq 'another tenant: actions' (Http GET "/api/wfsa/sessions/$OTHER/actions").s 404
    Assert-Eq 'another tenant: csv' (Http GET "/api/wfsa/sessions/$OTHER/export.csv").s 404

    Write-Host '== CSV export'
    $csvr = Http GET "/api/wfsa/sessions/$($S.id)/export.csv"
    $bytes = $csvr.r.RawContentStream.ToArray()
    Assert-Eq 'status' $csvr.s 200
    Assert-That 'content type and download name' (($csvr.r.Headers['Content-Type'] -join '') -like 'text/csv*' -and ($csvr.r.Headers['Content-Disposition'] -join '') -like 'attachment; filename=session-*.csv*')
    Assert-That 'UTF-8 BOM' ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $csv = @([System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Csv)
    Assert-Eq 'row count' $csv.Count 7
    Assert-Eq 'server name column' $csv[0].Server 'fs01'
    $inj = $csv | Where-Object { $_.Path -like 'D:\Finance\a,*' }
    Assert-Eq 'commas and quotes round-trip' $inj.Path 'D:\Finance\a,"b".txt'
    Assert-Eq 'a formula-looking name is neutralised' $inj.Who "'=SUM(1)"
    Assert-Eq 'a formula-looking process is neutralised' $inj.Process "'@evil"
    Assert-Eq 'denied row' ($csv | Where-Object { $_.Path -like '*denied.txt' }).Allowed 'Denied'
    Assert-Eq 'filtered export' @(([System.Text.Encoding]::UTF8.GetString((Http GET "/api/wfsa/sessions/$($S.id)/export.csv?operation=Modified").r.RawContentStream.ToArray()).TrimStart([char]0xFEFF) | ConvertFrom-Csv)).Count 2

    Write-Host '== a stopped session has a closed time window'
    $null = Http POST "/api/wfsa/sessions/$($S.id)/stop"
    $sc = Hub-Next $agent 'StopScope'; Answer-Command $agent $sc | Out-Null
    Assert-Eq 'S is Completed' (P "select state from wfsa.sessions where id='$($S.id)'") 'Completed'
    $null = Hub-Drain $ui 500
    $late = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @((Ev 12 'Created' 'D:\Finance\after.txt' ([DateTimeOffset]::UtcNow.AddHours(1)))) }
    Assert-Eq 'late event stored' $late.stored 1
    $lm = @(Hub-Drain $ui 1500)
    Assert-Eq 'not pushed to the stopped session' @($lm | Where-Object { $_.target -eq 'ActionsAppended' -and $_.arguments[0] -eq $S.id }).Count 0
    Assert-Eq 'pushed to the active session' @($lm | Where-Object { $_.target -eq 'ActionsAppended' -and $_.arguments[0] -eq $T.id }).Count 1
    Assert-Eq 'stopped session report is unchanged' (Seqs (Http GET "/api/wfsa/sessions/$($S.id)/actions").j.items) '11,9,8,7,6,2,1'
    Assert-Eq 'active session report includes it' (Seqs (Http GET "/api/wfsa/sessions/$($T.id)/actions").j.items) '12,11,9,8,7,6,1'

    Write-Host '== dashboard'
    $d = (Http GET '/api/wfsa/dashboard').j
    $chToday = Q "select sum(actions) from wfsa.daily_action_counts where tenant_id='$script:DevTenant' and day = today()"
    $chDenied = Q "select sumIf(actions, success=0) from wfsa.daily_action_counts where tenant_id='$script:DevTenant' and day = today()"
    Assert-Eq 'eventsToday matches ClickHouse' $d.eventsToday $chToday
    Assert-Eq 'deniedToday matches ClickHouse' $d.deniedToday $chDenied
    Assert-Eq 'active sessions (the other tenant is excluded)' $d.activeSessions 1
    Assert-Eq 'per-day series has today' @($d.perDay).Count 1
    Assert-That 'breakdowns are present' (@($d.byOperation).Count -gt 0 -and @($d.topActors).Count -gt 0)
    Assert-Eq 'days is clamped to 90' (Http GET '/api/wfsa/dashboard?days=500').j.days 90
    Assert-Eq 'days is clamped to 1' (Http GET '/api/wfsa/dashboard?days=0').j.days 1

    $unexpected = @(Get-LogLines $app 'Exception:' | Where-Object { $_ -notlike '*HubException: Unknown session.*' })
    Assert-Eq 'no exceptions in the log other than the 2 rejected JoinSession calls' $unexpected.Count 0
    Assert-Eq 'exactly 2 rejections were logged' (Get-LogLines $app 'Exception:').Count 2
}
catch { Assert-That 'script ran without an unexpected error' $false "$($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }
finally { Hub-Close $agent; Hub-Close $ui; Stop-App $app; Reset-Data }
Finish-Tests '05-reports'
