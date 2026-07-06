$ErrorActionPreference = "Stop"
$scriptRootCandidates = @()
if ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $scriptRootCandidates += $env:SAP_RPA_DEPLOY_SCRIPT_ROOT }
if ($PSScriptRoot) { $scriptRootCandidates += $PSScriptRoot }
if ($MyInvocation.MyCommand.Path) { $scriptRootCandidates += (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$scriptRootCandidates += (Join-Path (Get-Location) "scripts")
$commonPath = $null
foreach ($candidate in $scriptRootCandidates) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    $path = Join-Path $candidate "common.ps1"
    if (Test-Path -LiteralPath $path) { $commonPath = $path; break }
}
if (-not $commonPath) { throw "Cannot locate common.ps1. Set SAP_RPA_DEPLOY_SCRIPT_ROOT to the deployment scripts directory." }
$commonScript = Get-Content -LiteralPath $commonPath -Raw -Encoding UTF8
. ([ScriptBlock]::Create($commonScript))

$env:SAP_RPA_HOME = $RuntimeRoot
$launcher = Get-LauncherExe

Write-Step "Checking required files"
foreach ($path in @($RuntimeIndex, (Join-Path $RuntimeTransactions "ZFI072A.vbs"), (Join-Path $RuntimeData "sap-rpa-config.db"), $launcher)) {
    if (Test-Path $path) { Write-Ok $path } else { Write-Fail $path }
}

Write-Step "Running launcher self-test"
& $launcher test

Write-Step "Checking API"
foreach ($url in @(
    "http://127.0.0.1:8080/api/health",
    "http://127.0.0.1:8080/api/config",
    "http://127.0.0.1:8080/api/schema"
)) {
    try {
        $result = Invoke-RestMethod $url -TimeoutSec 8
        Write-Ok $url
        $result | ConvertTo-Json -Depth 5
    } catch {
        Write-Fail "$url - $($_.Exception.Message)"
    }
}

Write-Step "Sensitive data check reminder"
Write-Host "Page and API must not return SAP password, notification webhook plaintext, or secret plaintext."
