param(
    [string]$RuntimeRoot = "D:\RPA",
    [string]$HttpCompatibilityHost = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$gatewayScript = Join-Path $RuntimeRoot "gateway\rpa-gateway.js"
$launcher = Join-Path $RuntimeRoot "bin\SapWebLauncher.exe"

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
            # Internal servers may be unable to reach CRL/OCSP endpoints.
            # This keeps certificate chain and hostname validation, but skips
            # revocation lookup so the health check does not report a false outage.
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

function Resolve-HttpCompatibilityHost {
    param([string]$ConfiguredHost)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredHost)) {
        return $ConfiguredHost.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($env:RPA_HTTP_COMPATIBILITY_HOST)) {
        return $env:RPA_HTTP_COMPATIBILITY_HOST.Trim()
    }

    try {
        $address = [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
            Where-Object {
                $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork -and
                $_.IPAddressToString -notlike "127.*" -and
                $_.IPAddressToString -notlike "169.254.*"
            } |
            Select-Object -First 1

        if ($address) {
            return $address.IPAddressToString
        }
    } catch {
        Write-Warning "Unable to auto-detect local IPv4 for HTTP compatibility URL: $($_.Exception.Message)"
    }

    return "127.0.0.1"
}

Write-Host "SAP GUI COM check:"
$sapComResults = @("SapROTWr.SapROTWrapper", "Sapgui.ScriptingCtrl.1") | ForEach-Object {
    $registered = $false
    try {
        $registered = $null -ne [type]::GetTypeFromProgID($_)
    } catch {
        $registered = $false
    }
    [pscustomobject]@{ ProgId = $_; Registered = $registered }
}
$sapComResults | Format-Table -AutoSize
if (@($sapComResults | Where-Object { -not $_.Registered }).Count -gt 0) {
    Write-Warning "SAP GUI scripting COM registration is incomplete. Run 00_register_sap_gui_components.cmd from the startup scripts folder under $RuntimeRoot."
}

Write-Host "Process check:"
Get-CimInstance Win32_Process |
    Where-Object {
        $_.CommandLine -and (
            $_.CommandLine -like "*$launcher*--serve*" -or
            $_.CommandLine -like "*$gatewayScript*"
        )
    } |
    Select-Object ProcessId,Name,CommandLine |
    Format-List

Write-Host "Port check:"
netstat -ano | Select-String -Pattern "0\.0\.0\.0:443\s|0\.0\.0\.0:6174\s|127\.0\.0\.1:8080\s"

Write-Host "URL check:"
$resolvedHttpCompatibilityHost = Resolve-HttpCompatibilityHost -ConfiguredHost $HttpCompatibilityHost
$httpCompatibilityUrl = "http://${resolvedHttpCompatibilityHost}:6174/rpa/"
Write-Host "HTTP compatibility host: $resolvedHttpCompatibilityHost"

$checks = @(
    @{ Name = "Local API"; Uri = "http://127.0.0.1:8080/api/health" },
    @{ Name = "HTTPS portal"; Uri = "https://fi_automation.srv.lstech.com/rpa/" },
    @{ Name = "HTTPS API proxy"; Uri = "https://fi_automation.srv.lstech.com/rpa/api/health" },
    @{ Name = "HTTP compatibility portal"; Uri = $httpCompatibilityUrl }
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
