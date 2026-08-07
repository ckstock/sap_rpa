param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$HttpCompatibilityHost = "10.0.2.120",
    [int]$IntervalSeconds = 10
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$startScript = Join-Path $PSScriptRoot "01_start_sap_rpa_services.ps1"
$logDirectory = Join-Path $RuntimeRoot "logs"
$logFile = Join-Path $logDirectory "sap-rpa-startup-monitor.log"
$lockFile = Join-Path $logDirectory "sap-rpa-startup-monitor.lock"

if (-not (Test-Path -LiteralPath $startScript)) {
    throw "SAP RPA start script not found: $startScript"
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

try {
    # Only one monitor may control the gateway in an interactive SAP session.
    $monitorLock = [System.IO.File]::Open($lockFile, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
catch {
    exit 0
}

function Write-MonitorLog {
    param([string]$Message)

    $line = "{0:yyyy-MM-dd HH:mm:ss} [startup-monitor] {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

function Test-Http200 {
    param([string]$Uri)

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 5
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

Write-MonitorLog "started; intervalSeconds=$IntervalSeconds"

try {
    while ($true) {
        $apiReady = Test-Http200 "http://127.0.0.1:8080/api/health"
        $gatewayReady = Test-Http200 "http://127.0.0.1:6174/rpa/"

        if (-not $apiReady -or -not $gatewayReady) {
            Write-MonitorLog "recovery required; apiReady=$apiReady gatewayReady=$gatewayReady"
            try {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $startScript -RuntimeRoot $RuntimeRoot -HttpCompatibilityHost $HttpCompatibilityHost 2>&1 |
                    ForEach-Object { Add-Content -LiteralPath $logFile -Value ("{0:yyyy-MM-dd HH:mm:ss} [startup-monitor] {1}" -f (Get-Date), $_) -Encoding UTF8 }
                Write-MonitorLog "recovery command completed; exitCode=$LASTEXITCODE"
            }
            catch {
                Write-MonitorLog "recovery command failed: $($_.Exception.Message)"
            }
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    $monitorLock.Dispose()
}
