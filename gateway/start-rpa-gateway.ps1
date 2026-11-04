param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$NodePath = "D:\Program Files\nodejs\node.exe",
    [string]$PublicPath = "/rpa",
    [string]$ApiTarget = "http://127.0.0.1:8080",
    [string]$HttpsHost = "0.0.0.0",
    [string]$HttpHost = "0.0.0.0",
    [int]$HttpPort = 6174,
    [int]$HttpsPort = 443,
    [string]$CertDir = "D:\RPA\certs\lstech.com"
)

$ErrorActionPreference = "Stop"

$GatewayScript = Join-Path $RuntimeRoot "gateway\rpa-gateway.js"
$LogDir = Join-Path $RuntimeRoot "logs"
$CertPath = Join-Path $CertDir "lstech.com.pem"
$KeyPath = Join-Path $CertDir "lstech.com.key"
$CaPath = Join-Path $CertDir "CA.pem"
$PassPath = Join-Path $CertDir "passwd.txt"

if (-not (Test-Path -LiteralPath $NodePath)) { throw "Node not found: $NodePath" }
if (-not (Test-Path -LiteralPath $GatewayScript)) { throw "Gateway script not found: $GatewayScript" }
if (-not (Test-Path -LiteralPath $CertPath)) { throw "TLS cert not found: $CertPath" }
if (-not (Test-Path -LiteralPath $KeyPath)) { throw "TLS key not found: $KeyPath" }
if (-not (Test-Path -LiteralPath $PassPath)) { throw "TLS passphrase file not found: $PassPath" }

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Get-PortListener {
    param([int]$Port)

    return Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Sort-Object OwningProcess |
        Select-Object -First 1
}

function Get-ProjectGatewayProcess {
    param([int]$Port)

    $listener = Get-PortListener -Port $Port
    if (-not $listener) { return $null }

    try {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)" -ErrorAction Stop
    } catch {
        return $null
    }

    if (-not $process -or $process.Name -ne "node.exe" -or -not $process.CommandLine) { return $null }
    if ($process.CommandLine.IndexOf($GatewayScript, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { return $null }
    return $process
}

function Test-GatewayHealth {
    param(
        [string]$Protocol,
        [int]$Port
    )

    $healthPath = "/" + $PublicPath.Trim("/") + "/api/health"
    $uri = "${Protocol}://127.0.0.1:${Port}${healthPath}"
    $previousCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
    try {
        if ($Protocol -eq "https") {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
        }
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5
        $payload = $response.Content | ConvertFrom-Json
        return [int]$response.StatusCode -eq 200 -and
            $payload.ok -eq $true -and
            [string]$payload.app -eq "SapWebLauncher Bridge"
    } catch {
        return $false
    } finally {
        [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
    }
}

function Wait-PortFree {
    param([int]$Port)

    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (-not (Get-PortListener -Port $Port)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Gateway port $Port did not close after stopping the unhealthy project gateway."
}

function Start-Gateway {
    param(
        [string]$Protocol,
        [int]$Port,
        [string]$HostName
    )

    $env:RPA_GATEWAY_PORT = [string]$Port
    $env:RPA_GATEWAY_HOST = $HostName
    $env:RPA_PUBLIC_PATH = $PublicPath
    $env:RPA_RUNTIME_ROOT = $RuntimeRoot
    $env:RPA_API_TARGET = $ApiTarget
    $env:RPA_GATEWAY_PROTOCOL = $Protocol

    if ($Protocol -eq "https") {
        $env:RPA_TLS_CERT = $CertPath
        $env:RPA_TLS_KEY = $KeyPath
        $env:RPA_TLS_CA = $CaPath
        $env:RPA_TLS_PASSPHRASE = (Get-Content -LiteralPath $PassPath -Raw).Trim()
    } else {
        Remove-Item Env:\RPA_TLS_CERT,Env:\RPA_TLS_KEY,Env:\RPA_TLS_CA,Env:\RPA_TLS_PASSPHRASE -ErrorAction SilentlyContinue
    }

    Start-Process -FilePath $NodePath -ArgumentList $GatewayScript -WorkingDirectory $RuntimeRoot -WindowStyle Hidden
}

function Ensure-Gateway {
    param(
        [string]$Protocol,
        [int]$Port,
        [string]$HostName
    )

    $listener = Get-PortListener -Port $Port
    if ($listener) {
        $projectProcess = Get-ProjectGatewayProcess -Port $Port
        if (-not $projectProcess) {
            throw "Port $Port is occupied by a process that is not the configured SAP RPA gateway; it will not be stopped or reused."
        }
        if (Test-GatewayHealth -Protocol $Protocol -Port $Port) {
            Write-Host "$($Protocol.ToUpperInvariant()) gateway is healthy on port $Port; reusing process $($projectProcess.ProcessId)."
            return
        }

        Write-Host "$($Protocol.ToUpperInvariant()) project gateway on port $Port is unhealthy; restarting process $($projectProcess.ProcessId)."
        Stop-Process -Id $projectProcess.ProcessId -Force
        Wait-PortFree -Port $Port
    }

    Start-Gateway -Protocol $Protocol -Port $Port -HostName $HostName
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ((Get-ProjectGatewayProcess -Port $Port) -and (Test-GatewayHealth -Protocol $Protocol -Port $Port)) {
            Write-Host "$($Protocol.ToUpperInvariant()) gateway started and passed health validation on port $Port."
            return
        }
    }
    throw "$($Protocol.ToUpperInvariant()) gateway did not pass process and health validation on port $Port."
}

Ensure-Gateway -Protocol "http" -Port $HttpPort -HostName $HttpHost
Ensure-Gateway -Protocol "https" -Port $HttpsPort -HostName $HttpsHost

Start-Sleep -Seconds 1

$netstat = netstat -ano
$httpListener = $netstat | Select-String -Pattern "0\.0\.0\.0:$HttpPort\s"
$httpsListener = $netstat | Select-String -Pattern "0\.0\.0\.0:$HttpsPort\s"
if (-not $httpListener -or -not $httpsListener) {
    throw "Gateway listeners were not both detected after start. HTTP=$([bool]$httpListener) HTTPS=$([bool]$httpsListener)"
}

$httpListener
$httpsListener
