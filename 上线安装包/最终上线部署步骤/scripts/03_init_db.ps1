$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

Ensure-RuntimeDirs
$env:SAP_RPA_HOME = $RuntimeRoot
$launcher = Get-LauncherExe

Write-Step "Initializing SQLite: $(Join-Path $RuntimeData 'sap-rpa-config.db')"
& $launcher --init-db
if ($LASTEXITCODE -ne 0) { throw "SQLite initialization failed." }

Write-Ok "SQLite initialization completed"
