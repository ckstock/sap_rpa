$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

Write-Step "Preparing runtime directory: $RuntimeRoot"
Ensure-RuntimeDirs

if (Test-Path (Join-Path $RepoRoot "index.html")) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot "index.html") -Destination $RuntimeIndex -Force
    Write-Ok "Page copied: $RuntimeIndex"
} else {
    Write-Warn "Source page not found, skipped: $(Join-Path $RepoRoot 'index.html')"
}

if (Test-Path $SourceTransactions) {
    Copy-Item -Path (Join-Path $SourceTransactions "*") -Destination $RuntimeTransactions -Recurse -Force
    Write-Ok "VBS files copied: $RuntimeTransactions"
} else {
    Write-Warn "Source transactions directory not found, skipped: $SourceTransactions"
}

Write-Ok "Runtime directory is ready"
