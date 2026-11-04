param(
    [string]$RuntimeRoot = "D:\RPA"
)

$ErrorActionPreference = "Stop"
$launcher = Join-Path $RuntimeRoot "bin\SapWebLauncher.exe"

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher not found: $launcher"
}

$currentIdentity = "$env:USERDOMAIN\$env:USERNAME"
if ($env:USERNAME -notmatch "^marcus$") {
    throw "Run this script while logged in as marcus. Current identity: $currentIdentity"
}

$queue = Invoke-RestMethod -Uri "http://127.0.0.1:8080/api/queue/status" -TimeoutSec 10
if ($queue.running -or [int]$queue.queuedCount -gt 0) {
    throw "Queue is not empty. running=$($queue.running), queued=$($queue.queuedCount). Wait for all tasks to finish before switching desktop mode."
}

Write-Host "Stopping the boot-mode monitor and API..."
Stop-ScheduledTask -TaskName "SAP_RPA_StartupMonitor"
Start-Sleep -Seconds 2

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

Write-Host "Starting the API in the current interactive desktop session..."
Start-Process -FilePath $launcher -ArgumentList "--serve" -WorkingDirectory $RuntimeRoot
Start-Sleep -Seconds 5

$health = Invoke-RestMethod -Uri "http://127.0.0.1:8080/api/health" -TimeoutSec 15
if (-not $health.ok) {
    throw "Interactive API did not become healthy."
}

Write-Host "Interactive API is ready under $currentIdentity. SAP GUI should appear in this desktop session when a task starts."
Write-Host "After observation, run D:\RPA\启动脚本\restore_sap_rpa_background.cmd."
