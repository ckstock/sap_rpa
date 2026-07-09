param(
    [string]$RuntimeRoot = "D:\RPA"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$gatewayScript = Join-Path $RuntimeRoot "gateway\rpa-gateway.js"
$launcher = Join-Path $RuntimeRoot "bin\SapWebLauncher.exe"

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
