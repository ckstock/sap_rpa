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

Write-Step "Preparing runtime directory: $RuntimeRoot"
Ensure-RuntimeDirs

if (Test-Path (Join-Path $RepoRoot "index.html")) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot "index.html") -Destination $RuntimeIndex -Force
    Write-Ok "Page copied: $RuntimeIndex"
} else {
    Write-Warn "Source page not found, skipped: $(Join-Path $RepoRoot 'index.html')"
}

if (Test-Path (Join-Path $RepoRoot "assets")) {
    New-Item -ItemType Directory -Force -Path $RuntimeAssets | Out-Null
    Copy-Item -Path (Join-Path $RepoRoot "assets\*") -Destination $RuntimeAssets -Recurse -Force
    Write-Ok "Frontend assets copied: $RuntimeAssets"
} else {
    Write-Warn "Source assets directory not found, skipped: $(Join-Path $RepoRoot 'assets')"
}

if (Test-Path $SourceTransactions) {
    Copy-Item -Path (Join-Path $SourceTransactions "*") -Destination $RuntimeTransactions -Recurse -Force
    Write-Ok "VBS files copied: $RuntimeTransactions"
} else {
    Write-Warn "Source transactions directory not found, skipped: $SourceTransactions"
}

Write-Ok "Runtime directory is ready"
