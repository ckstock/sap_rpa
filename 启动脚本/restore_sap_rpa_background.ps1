param(
    [string]$RuntimeRoot = "D:\RPA"
)

$ErrorActionPreference = "Stop"
$launcher = Join-Path $RuntimeRoot "bin\SapWebLauncher.exe"

$queue = Invoke-RestMethod -Uri "http://127.0.0.1:8080/api/queue/status" -TimeoutSec 10
if ($queue.running -or [int]$queue.queuedCount -gt 0) {
    throw "Queue is not empty. running=$($queue.running), queued=$($queue.queuedCount). Finish all tasks before restoring background mode."
}

Write-Host "Stopping the interactive API..."
$launcherProcesses = Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -eq "SapWebLauncher.exe" -and
        $_.CommandLine -and
        $_.CommandLine -like "*$launcher*--serve*"
    }
foreach ($process in $launcherProcesses) {
    Stop-Process -Id $process.ProcessId -Force
}
Start-Sleep -Seconds 3

Write-Host "Starting the boot-mode API and monitor..."
Start-ScheduledTask -TaskName "SAP_RPA_AutoStart"
Start-Sleep -Seconds 5
Start-ScheduledTask -TaskName "SAP_RPA_StartupMonitor"
Start-Sleep -Seconds 3

$health = Invoke-RestMethod -Uri "http://127.0.0.1:8080/api/health" -TimeoutSec 15
if (-not $health.ok) {
    throw "Background API did not become healthy."
}

Write-Host "Background API is ready. SAP GUI will run in the boot session and may not be visible on the desktop."
