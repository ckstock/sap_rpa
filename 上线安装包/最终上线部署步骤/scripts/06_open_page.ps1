$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

if (-not (Test-Path $RuntimeIndex)) {
    throw "Page not found: $RuntimeIndex. Run the runtime preparation step first."
}

Write-Step "Opening runtime page"
Start-Process $RuntimeIndex
Write-Ok $RuntimeIndex
