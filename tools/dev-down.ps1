# Stops the processes started by dev-up.ps1 (and their children). The database containers are left running.
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$pidFile = Join-Path $root '.dev\pids.json'
$havePids = Test-Path $pidFile

$pids = if ($havePids) { Get-Content $pidFile | ConvertFrom-Json } else { @{} }
foreach ($name in 'frontend', 'agent', 'backend') {
    $id = $pids.$name
    if ($id) {
        # /T also ends children (npm -> node, dotnet run -> server).
        taskkill /PID $id /T /F 2>$null | Out-Null
        Write-Host "stopped $name ($id)"
    }
}
if ($havePids) { Remove-Item $pidFile }

# The Next dev server re-spawns itself, so it can outlive the process tree above. Stop anything still listening on a
# dev port, but only if its command line points into this repository.
foreach ($port in 3000, 5044) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.OwningProcess)"
        if ($proc.CommandLine -and $proc.CommandLine.Contains($root.Path)) {
            Stop-Process -Id $proc.ProcessId -Force
            Write-Host "stopped leftover process on port $port ($($proc.ProcessId))"
        }
    }
}
