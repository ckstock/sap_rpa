param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$HttpCompatibilityHost = "10.0.2.120",
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [ValidateSet("AtStartup", "AtLogon")]
    [string]$TriggerMode = "AtLogon"
)

$ErrorActionPreference = "Stop"

$startScript = Join-Path $PSScriptRoot "01_start_sap_rpa_services.ps1"
$monitorScript = Join-Path $PSScriptRoot "04_monitor_sap_rpa_services.ps1"
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

if (-not (Test-Path -LiteralPath $startScript)) {
    throw "SAP RPA start script not found: $startScript"
}
if (-not (Test-Path -LiteralPath $monitorScript)) {
    throw "SAP RPA monitor script not found: $monitorScript"
}
if ([string]::IsNullOrWhiteSpace($RunAsUser)) {
    throw "RunAsUser is required. Use the fixed SAP execution account, not a temporary bastion account."
}

Write-Host "The startup tasks will run as: $RunAsUser"
Write-Host "This account must be the same account that owns the SAP DPAPI configuration."
$isInteractive = $TriggerMode -eq "AtLogon"
$securePassword = $null
$passwordPtr = [IntPtr]::Zero
$plainPassword = $null

try {
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew
    if ($isInteractive) {
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
        $principal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType Interactive -RunLevel Limited
        Write-Host "Trigger mode: AtLogon / InteractiveToken / Limited (SAP GUI will be visible in the marcus desktop session)."
    }
    else {
        $securePassword = Read-Host "Enter the password for $RunAsUser (used only to register the Windows startup tasks)" -AsSecureString
        $passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr)
        $trigger = New-ScheduledTaskTrigger -AtStartup
        Write-Host "Trigger mode: AtStartup / background Session 0 (GUI will not be visible)."
    }

    $startArguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$startScript`" -RuntimeRoot `"$RuntimeRoot`" -HttpCompatibilityHost `"$HttpCompatibilityHost`""
    $monitorArguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$monitorScript`" -RuntimeRoot `"$RuntimeRoot`" -HttpCompatibilityHost `"$HttpCompatibilityHost`" -IntervalSeconds 10"
    $startAction = New-ScheduledTaskAction -Execute $powershell -Argument $startArguments
    $monitorAction = New-ScheduledTaskAction -Execute $powershell -Argument $monitorArguments

    $startDescription = if ($isInteractive) { "Start SAP RPA API and gateways when the fixed SAP account logs on; keep SAP GUI visible." } else { "Start SAP RPA API and gateways when Windows starts; do not trigger on user logon." }
    $monitorDescription = if ($isInteractive) { "Keep SAP RPA API and gateways healthy after the fixed SAP account logs on; keep SAP GUI visible." } else { "Keep SAP RPA API and gateways healthy after Windows startup; do not trigger on user logon." }
    if ($isInteractive) {
        Register-ScheduledTask -TaskName "SAP_RPA_AutoStart" -Action $startAction -Trigger $trigger -Settings $settings -Principal $principal -Description $startDescription -Force | Out-Null
        Register-ScheduledTask -TaskName "SAP_RPA_StartupMonitor" -Action $monitorAction -Trigger $trigger -Settings $settings -Principal $principal -Description $monitorDescription -Force | Out-Null
    }
    else {
        Register-ScheduledTask -TaskName "SAP_RPA_AutoStart" -Action $startAction -Trigger $trigger -Settings $settings -User $RunAsUser -Password $plainPassword -RunLevel Highest -Description $startDescription -Force | Out-Null
        Register-ScheduledTask -TaskName "SAP_RPA_StartupMonitor" -Action $monitorAction -Trigger $trigger -Settings $settings -User $RunAsUser -Password $plainPassword -RunLevel Highest -Description $monitorDescription -Force | Out-Null
    }

    Write-Host "Registered SAP_RPA_AutoStart and SAP_RPA_StartupMonitor with a $TriggerMode trigger."
    Get-ScheduledTask -TaskName "SAP_RPA_AutoStart", "SAP_RPA_StartupMonitor" |
        Select-Object TaskName, State, Principal, Triggers |
        Format-List
}
finally {
    if ($passwordPtr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr)
    }
    $plainPassword = $null
}
