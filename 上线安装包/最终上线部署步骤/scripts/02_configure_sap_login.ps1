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

$configureScript = Join-Path $RepoRoot "上线安装包\scripts\configure_sap_login.ps1"
if (-not (Test-Path $configureScript)) {
    throw "SAP login configure script not found: $configureScript"
}

Write-Step "Configuring SAP login. Password will be protected by Windows DPAPI for the current user."
$script = Get-Content -LiteralPath $configureScript -Raw -Encoding UTF8
& ([ScriptBlock]::Create($script))

Write-Ok "SAP login configuration completed"
