# B0/B1 + CORS: health, hub auth, migrations, seeding, idempotent restart, configuration override.
. "$PSScriptRoot\Common.ps1"
Reset-Data
$app = $null
try {
    Write-Host '== startup and seed'
    $app = Start-App
    Assert-That 'first start prints the dev agent key' ([bool]$app.Key)
    $key = $app.Key

    Assert-Eq 'GET /health' (Http GET '/health').s 200
    Assert-Eq 'GET /api/wfsa/ping' (Http GET '/api/wfsa/ping').j.module 'wfsa'
    Assert-Eq 'removed probe /dev/me is gone' (Http GET '/dev/me').s 404

    Write-Host '== databases'
    Assert-Eq 'PG schemas' (P "select string_agg(schema_name, ',' order by schema_name) from information_schema.schemata where schema_name in ('core','wfsa','audit','ddc')") 'core,ddc,wfsa'
    Assert-Eq 'core tables' (P "select count(*) from information_schema.tables where table_schema='core'") 8
    Assert-Eq 'wfsa tables' (P "select count(*) from information_schema.tables where table_schema='wfsa'") 7
    Assert-Eq 'wfsa migrations tracked' (P "select string_agg(version, ',' order by version) from wfsa.schema_migrations") '0001_wfsa.sql,0002_ddc_contract.sql,0003_read_batches.sql'
    Assert-Eq 'ClickHouse wfsa tables' (Q "select arrayStringConcat(arraySort(groupArray(name)), ',') from system.tables where database='wfsa'") 'actions,daily_action_counts,daily_action_counts_mv,read_actions'
    Assert-Eq 'ClickHouse has no audit database' (Q "select count() from system.databases where name='audit'") 0
    Assert-Eq 'seed: one tenant, user, agent' (P "select (select count(*) from core.tenants)||'/'||(select count(*) from core.users)||'/'||(select count(*) from core.agents)") '1/1/1'
    Assert-Eq 'agent key stored as a 32-byte hash' (P "select length(api_key_hash) from core.agents where name='dev-agent'") 32

    Write-Host '== hub authentication'
    Assert-Eq 'ui hub negotiate' (Http POST '/hubs/ui/negotiate?negotiateVersion=1').s 200
    Assert-Eq 'agent hub, no key' (Http POST '/hubs/agent/negotiate?negotiateVersion=1').s 401
    Assert-Eq 'agent hub, wrong key' (Http POST '/hubs/agent/negotiate?negotiateVersion=1' $null @{ 'X-Agent-Key' = 'wrong' }).s 401
    Assert-Eq 'agent hub, real key' (Http POST '/hubs/agent/negotiate?negotiateVersion=1' $null @{ 'X-Agent-Key' = $key }).s 200

    Write-Host '== CORS'
    $pre = Http OPTIONS '/api/servers' $null @{ Origin = 'http://localhost:3000'; 'Access-Control-Request-Method' = 'POST'; 'Access-Control-Request-Headers' = 'content-type' }
    Assert-Eq 'preflight from the allowed origin' ($pre.r.Headers['Access-Control-Allow-Origin'] -join '') 'http://localhost:3000'
    Assert-Eq 'preflight allows credentials' ($pre.r.Headers['Access-Control-Allow-Credentials'] -join '') 'true'
    $bad = Http OPTIONS '/api/servers' $null @{ Origin = 'http://evil.example'; 'Access-Control-Request-Method' = 'POST' }
    Assert-Eq 'preflight from another origin gets no allow header' ($bad.r.Headers['Access-Control-Allow-Origin'] -join '') ''
    $neg = Http POST '/hubs/ui/negotiate?negotiateVersion=1' $null @{ Origin = 'http://localhost:3000' }
    Assert-Eq 'hub negotiate allows the frontend origin' ($neg.r.Headers['Access-Control-Allow-Origin'] -join '') 'http://localhost:3000'

    Write-Host '== OpenAPI contract and Swagger UI (Development)'
    $docResponse = Http GET '/openapi/v1.json'
    Assert-Eq 'openapi document served' $docResponse.s 200
    $doc = $docResponse.j
    Assert-Eq 'document title' $doc.info.title 'Auditor API'
    $paths = @($doc.paths.PSObject.Properties.Name)
    foreach ($expected in '/health', '/api/wfsa/ping', '/api/servers', '/api/servers/{id}/approve', '/api/shares', '/api/wfsa/sessions', '/api/wfsa/sessions/{id}', '/api/wfsa/sessions/{id}/stop', '/api/wfsa/sessions/{id}/actions', '/api/wfsa/sessions/{id}/export.csv', '/api/wfsa/dashboard') {
        Assert-That "path $expected" ($paths -contains $expected)
    }
    Assert-That 'hubs are not in the REST contract' (-not ($paths | Where-Object { $_ -like '/hubs*' }))
    $schemas = $doc.components.schemas
    Assert-Eq 'session states are strings' ((@($schemas.SessionState.enum) -join ',')) 'Requested,Starting,Active,Degraded,Stopping,Completed,Failed'
    Assert-That 'operations are strings' (@($schemas.Operation.enum) -contains 'ContentView')
    Assert-That 'error schema' ($null -ne $schemas.ApiError.properties.error)
    Assert-That 'report row schema' ($null -ne $schemas.ActionRow.properties.complianceTags -and $null -ne $schemas.ActionRow.properties.riskLevel)
    $create = $doc.paths.'/api/wfsa/sessions'.post.responses
    Assert-That 'session create documents 201, 400 and 409' ($null -ne $create.'201' -and $null -ne $create.'400' -and $null -ne $create.'409')
    Assert-That 'csv export documents text/csv' ($null -ne $doc.paths.'/api/wfsa/sessions/{id}/export.csv'.get.responses.'200'.content.'text/csv')
    Assert-That 'endpoints are tagged' (@($doc.paths.'/api/servers'.get.tags) -contains 'Servers' -and @($doc.paths.'/api/wfsa/dashboard'.get.tags) -contains 'Reports')
    Assert-Eq 'swagger ui page' (Http GET '/swagger/index.html').s 200

    Write-Host '== restart is idempotent'
    Stop-App $app
    $app = Start-App -KeepAgent
    Assert-That 'second start does not print a key' (-not $app.Key)
    Assert-Eq 'second start still healthy' (Http GET '/health').s 200
    Assert-Eq 'same key still accepted' (Http POST '/hubs/agent/negotiate?negotiateVersion=1' $null @{ 'X-Agent-Key' = $key }).s 200
    Assert-Eq 'still one agent' (P 'select count(*) from core.agents') 1
    Stop-App $app

    Write-Host '== configuration override'
    $app = Start-App -KeepAgent -AllowUnhealthy -EnvVars @{ 'ConnectionStrings__Postgres' = 'Host=localhost;Port=5999;Database=postgres;Username=postgres;Password=x' }
    Assert-That 'a bad ConnectionStrings__Postgres env var stops startup' (-not $app.Healthy)
    Stop-App $app

    Write-Host '== Production does not publish the contract or the UI'
    $app = Start-App -KeepAgent -NoLaunchProfile -EnvVars @{ ASPNETCORE_ENVIRONMENT = 'Production' }
    Assert-Eq 'health still works' (Http GET '/health').s 200
    Assert-Eq 'openapi document is not served' (Http GET '/openapi/v1.json').s 404
    Assert-Eq 'swagger ui is not served' (Http GET '/swagger/index.html').s 404
}
catch { Assert-That 'script ran without an unexpected error' $false $_.Exception.Message }
finally { Stop-App $app; Reset-Data }
Finish-Tests '01-health-auth'
