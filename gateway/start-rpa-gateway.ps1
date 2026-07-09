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

Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$GatewayScript*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 2

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

Start-Gateway -Protocol "http" -Port $HttpPort -HostName $HttpHost
Start-Gateway -Protocol "https" -Port $HttpsPort -HostName $HttpsHost
Start-Sleep -Seconds 3

$netstat = netstat -ano
$httpListener = $netstat | Select-String -Pattern "0\.0\.0\.0:$HttpPort\s"
$httpsListener = $netstat | Select-String -Pattern "0\.0\.0\.0:$HttpsPort\s"
if (-not $httpListener -or -not $httpsListener) {
    throw "Gateway listeners were not both detected after start. HTTP=$([bool]$httpListener) HTTPS=$([bool]$httpsListener)"
}

$httpListener
$httpsListener
