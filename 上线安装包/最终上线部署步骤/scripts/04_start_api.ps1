$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

Ensure-RuntimeDirs
$launcher = Get-LauncherExe
$env:SAP_RPA_HOME = $RuntimeRoot

$existing = Get-CimInstance Win32_Process -Filter "name = 'SapWebLauncher.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match '--serve| serve' }

if ($existing) {
    Write-Ok "Local API is already running. Process ID: $($existing.ProcessId -join ', ')"
    exit 0
}

Write-Step "Starting local API: $launcher --serve"
Start-Process -FilePath $launcher -ArgumentList "--serve" -WorkingDirectory (Split-Path -Parent $launcher) -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
    $health = Invoke-RestMethod "http://127.0.0.1:17890/api/health" -TimeoutSec 5
    Write-Ok "API started: http://127.0.0.1:17890/api/health"
    $health | ConvertTo-Json -Depth 6
} catch {
    Write-Warn "API has not responded yet. Run the status check later. Error: $($_.Exception.Message)"
}
