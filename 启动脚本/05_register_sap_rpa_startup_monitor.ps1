param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$HttpCompatibilityHost = "10.0.2.120",
    [string]$UserId = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = "Stop"

$monitor = Join-Path $PSScriptRoot "04_monitor_sap_rpa_services.ps1"
if (-not (Test-Path -LiteralPath $monitor)) {
    throw "SAP RPA monitor script not found: $monitor"
}

$taskName = "SAP_RPA_StartupMonitor"
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$monitor`" -RuntimeRoot `"$RuntimeRoot`" -HttpCompatibilityHost `"$HttpCompatibilityHost`" -IntervalSeconds 10"
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description "SAP RPA 10-second API and gateway recovery monitor." -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Get-ScheduledTaskInfo -TaskName $taskName | Format-List LastRunTime,LastTaskResult
