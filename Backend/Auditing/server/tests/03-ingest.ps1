# B3: agent ingest, dedup, DDC stamping, positions, reads, rejections.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null; $agent = $null
try {
    $app = Start-App
    $fx = New-Fixture; $sid = $fx.ServerId
    Pg-Exec "insert into ddc.file_classification(tenant_id,server_id,path_hash,path,content_type,risk_level,compliance_tags,monetary_value,classified_at) values ('$script:DevTenant','$sid',1001,'D:\Finance\a.xlsx','Financial',3,array['PCI','SOX'],1250.50,now())"
    $t0 = [DateTimeOffset]::UtcNow
    function A($seq, $op, $path, $min, $hash = $null) { New-Action $sid $seq $op $path $t0.AddMinutes($min) $true 'CORP\alice' 'explorer.exe' 'File' $hash }

    Write-Host '== authentication'
    $rejected = $false
    try { $bad = Hub-Connect '/hubs/agent' 'wrong'; $null = Hub-Call $bad 'EventBatch' @{ serverId = $sid; actions = @() } } catch { $rejected = $true }
    Assert-That 'a wrong key cannot connect' $rejected
    $agent = Hub-Connect '/hubs/agent' $app.Key

    Write-Host '== first batch (seq 1-3, seq 1 has a DDC row)'
    $b1 = @((A 1 'Created' 'D:\Finance\a.xlsx' 0 1001), (A 2 'Modified' 'D:\Finance\b.xlsx' 1), (A 3 'Deleted' 'D:\Finance\c.xlsx' 2))
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = $b1 }
    Assert-Eq 'ack stored/duplicates/lastSeq' "$($ack.stored)/$($ack.duplicates)/$($ack.lastSeq)" '3/0/3'
    Assert-Eq 'rows in ClickHouse' (Q 'select count() from wfsa.actions') 3
    Assert-Eq 'DDC fields stamped when a classification exists' (Q "select content_type||'|'||toString(risk_level)||'|'||arrayStringConcat(compliance_tags,',')||'|'||toString(monetary_value) from wfsa.actions where seq=1") 'Financial|3|PCI,SOX|1250.5'
    Assert-Eq 'DDC fields default when there is none' (Q "select content_type||'|'||toString(risk_level)||'|'||toString(length(compliance_tags)) from wfsa.actions where seq=2") '|-1|0'
    Assert-Eq 'position moved to 3' (P "select last_seq from wfsa.agent_positions where server_id='$sid'") 3
    Assert-Eq 'daily rollup counts 3' (Q 'select sum(actions) from wfsa.daily_action_counts') 3

    Write-Host '== resends'
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = $b1 }
    Assert-Eq 'identical resend: stored/duplicates' "$($ack.stored)/$($ack.duplicates)" '0/3'
    Assert-Eq 'no extra rows' (Q 'select count() from wfsa.actions') 3
    Assert-Eq 'rollup is not double counted' (Q 'select sum(actions) from wfsa.daily_action_counts') 3
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @((A 3 'Deleted' 'D:\Finance\c.xlsx' 2), (A 4 'Renamed' 'D:\Finance\d.xlsx' 3)) }
    Assert-Eq 'partial overlap: stored/duplicates' "$($ack.stored)/$($ack.duplicates)" '1/1'
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @((A 2 'Modified' 'D:\Finance\b.xlsx' 1)) }
    Assert-Eq 'an older batch is all duplicates' $ack.stored 0
    Assert-Eq 'the position never moves backwards' (P "select last_seq from wfsa.agent_positions where server_id='$sid'") 4
    $ack = Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @((A 5 'Created' 'D:\Finance\e.xlsx' 4), (A 5 'Created' 'D:\Finance\e.xlsx' 4)) }
    Assert-Eq 'a duplicate seq inside one batch is stored once' $ack.stored 1
    Assert-Eq 'total rows' (Q 'select count() from wfsa.actions') 5

    Write-Host '== read batch'
    $rb = [guid]::NewGuid()
    $rd = Hub-Call $agent 'ReadBatch' @{ batchId = $rb; serverId = $sid; reads = @(@{ bucketStart = $t0.ToString('o'); actorSid = 'S-1-5-21-1'; pathHash = 1001; firstAt = $t0.ToString('o'); lastAt = $t0.AddMinutes(5).ToString('o'); readCount = 7; objectType = 'File'; path = 'D:\Finance\a.xlsx'; actorName = 'CORP\alice'; clientIp = '10.0.0.5'; processName = 'excel.exe' }) }
    Assert-Eq 'read ack' $rd.stored 1
    Assert-Eq 'read row: hour bucket, count, DDC' (Q "select toString(toMinute(bucket_start))||'|'||toString(read_count)||'|'||content_type from wfsa.read_actions") '0|7|Financial'

    $rd2 = Hub-Call $agent 'ReadBatch' @{ batchId = $rb; serverId = $sid; reads = @(@{ bucketStart = $t0.ToString('o'); actorSid = 'S-1-5-21-1'; pathHash = 1001; firstAt = $t0.ToString('o'); lastAt = $t0.AddMinutes(5).ToString('o'); readCount = 7; objectType = 'File'; path = 'D:Finance.xlsx' }) }
    Assert-Eq 'a resent read batch is skipped' "$($rd2.stored)/$($rd2.duplicates)" '0/1'
    Assert-Eq 'the read count is not doubled' (Q 'select sum(read_count) from wfsa.read_actions') 7
    Assert-That 'a missing batchId is rejected' ((Hub-Call $agent 'ReadBatch' @{ serverId = $sid; reads = @(@{ bucketStart = $t0.ToString('o'); actorSid = 'x'; pathHash = 1; firstAt = $t0.ToString('o'); lastAt = $t0.ToString('o'); readCount = 1; objectType = 'File'; path = 'p' }) }).error -like '*BatchId*')

    Write-Host '== rejections'
    Assert-That 'unknown server' ((Hub-Call $agent 'EventBatch' @{ serverId = [guid]::NewGuid(); actions = @((A 9 'Created' 'x' 0)) }).error -like '*Unknown server*')
    $foreign = New-Action $sid 9 'Created' 'x' $t0; $foreign.tenantId = [guid]::NewGuid()
    Assert-That 'another tenant in an action' ((Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @($foreign) }).error -like '*does not belong*')
    $mismatch = New-Action ([guid]::NewGuid()) 9 'Created' 'x' $t0
    Assert-That 'another server inside an action' ((Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @($mismatch) }).error -like '*does not belong*')
    Assert-Eq 'an empty batch is acknowledged' (Hub-Call $agent 'EventBatch' @{ serverId = $sid; actions = @() }).stored 0
    Assert-Eq 'nothing was stored by the rejected batches' (Q 'select count() from wfsa.actions') 5
    Assert-Eq 'no exceptions escaped to the log other than the 4 rejections' (Get-LogLines $app 'Exception:').Count 4
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally { Hub-Close $agent; Stop-App $app; Reset-Data }
Finish-Tests '03-ingest'
