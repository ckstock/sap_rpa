param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$NodePath = "D:\Program Files\nodejs\node.exe"
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

$launcherProcess = Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq "SapWebLauncher.exe" -and $_.CommandLine -and $_.CommandLine -like "*$Launcher*--serve*" } |
    Select-Object -First 1

if ($launcherProcess) {
    Write-Host "SapWebLauncher API already running. PID=$($launcherProcess.ProcessId)"
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
        $response = Invoke-WebRequest -UseBasicParsing -Uri $check.Uri -TimeoutSec 15
        [pscustomobject]@{ Name = $check.Name; Uri = $check.Uri; Status = $response.StatusCode; Result = "OK" }
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
