param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$NodePath = "D:\Program Files\nodejs\node.exe",
    [switch]$RestartLauncher
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Launcher = Join-Path $RuntimeRoot "bin\SapWebLauncher.exe"
$GatewayStarter = Join-Path $RuntimeRoot "gateway\start-rpa-gateway.ps1"
$LogDir = Join-Path $RuntimeRoot "logs"

if (-not (Test-Path -LiteralPath $Launcher)) { throw "Launcher not found: $Launcher" }
if (-not (Test-Path -LiteralPath $GatewayStarter)) { throw "Gateway starter not found: $GatewayStarter" }

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

Write-Host "RuntimeRoot: $RuntimeRoot"
Write-Host "Launcher:    $Launcher"
Write-Host "Gateway:     $GatewayStarter"

function Invoke-UrlStatus {
    param([string]$Uri)

    $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
    if ($curl) {
        $curlArgs = @(
            "--silent",
            "--show-error",
            "--location",
            "--max-time", "15",
            "--output", "NUL",
            "--write-out", "%{http_code}"
        )

        if ($Uri.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
            $curlArgs = @("--ssl-no-revoke") + $curlArgs
        }

        $output = & $curl.Source @curlArgs $Uri 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw (($output | ForEach-Object { "$_" }) -join [Environment]::NewLine)
        }

        $statusText = (($output | Select-Object -Last 1) -as [string]).Trim()
        if ($statusText -notmatch "^\d{3}$") {
            throw "Unexpected curl status output: $statusText"
        }

        return [int]$statusText
    }

    if ($Uri.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
        [Net.ServicePointManager]::CheckCertificateRevocationList = $false
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }

    $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 15
    return [int]$response.StatusCode
}

$launcherProcess = Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq "SapWebLauncher.exe" -and $_.CommandLine -and $_.CommandLine -like "*$Launcher*--serve*" } |
    Select-Object -First 1

if ($launcherProcess -and $RestartLauncher) {
    Write-Host "Restarting SapWebLauncher API. PID=$($launcherProcess.ProcessId)"
    Stop-Process -Id $launcherProcess.ProcessId -Force
    Start-Sleep -Seconds 2
    $launcherProcess = $null
}

if ($launcherProcess) {
    Write-Host "SapWebLauncher API already running. PID=$($launcherProcess.ProcessId)"
    $localConfig = Join-Path $RuntimeRoot "config.local.json"
    if (Test-Path -LiteralPath $localConfig) {
        $configTime = (Get-Item -LiteralPath $localConfig).LastWriteTime
        $processStart = if ($launcherProcess.CreationDate -is [datetime]) {
            $launcherProcess.CreationDate
        } else {
            [Management.ManagementDateTimeConverter]::ToDateTime([string]$launcherProcess.CreationDate)
        }
        if ($configTime -gt $processStart) {
            Write-Warning "config.local.json is newer than the running SapWebLauncher process. Run this script with -RestartLauncher, or use 03_restart_sap_rpa_services.cmd, after config changes."
        }
    }
} else {
    Write-Host "Starting SapWebLauncher API..."
    Start-Process -FilePath $Launcher -ArgumentList "--serve" -WorkingDirectory $RuntimeRoot -WindowStyle Hidden
    Start-Sleep -Seconds 5
}

Write-Host "Starting RPA gateways..."
& powershell -ExecutionPolicy Bypass -File $GatewayStarter -RuntimeRoot $RuntimeRoot -NodePath $NodePath
Start-Sleep -Seconds 3

$checks = @(
    @{ Name = "Local API"; Uri = "http://127.0.0.1:8080/api/health" },
    @{ Name = "HTTPS portal"; Uri = "https://fi_automation.srv.lstech.com/rpa/" },
    @{ Name = "HTTPS API proxy"; Uri = "https://fi_automation.srv.lstech.com/rpa/api/health" },
    @{ Name = "HTTP compatibility portal"; Uri = "http://10.0.41.158:6174/rpa/" }
)

$results = foreach ($check in $checks) {
    try {
        $status = Invoke-UrlStatus -Uri $check.Uri
        $result = if ($status -eq 200) { "OK" } else { "HTTP $status" }
        [pscustomobject]@{ Name = $check.Name; Uri = $check.Uri; Status = $status; Result = $result }
    } catch {
        [pscustomobject]@{ Name = $check.Name; Uri = $check.Uri; Status = ""; Result = $_.Exception.Message }
    }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.Status -ne 200 })
if ($failed.Count -gt 0) {
    throw "One or more SAP RPA service checks failed."
}

Write-Host ""
Write-Host "SAP RPA services are ready:"
Write-Host "  https://fi_automation.srv.lstech.com/rpa/"
Write-Host "  http://10.0.41.158:6174/rpa/"
