$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

$configureScript = Join-Path $RepoRoot "上线安装包\scripts\configure_sap_login.ps1"
if (-not (Test-Path $configureScript)) {
    throw "SAP login configure script not found: $configureScript"
}

Write-Step "Configuring SAP login. Password will be protected by Windows DPAPI for the current user."
$script = Get-Content -LiteralPath $configureScript -Raw -Encoding UTF8
& ([ScriptBlock]::Create($script))

Write-Ok "SAP login configuration completed"
