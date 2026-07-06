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

Ensure-RuntimeDirs
$launcher = Get-LauncherExe
$env:SAP_RPA_HOME = $RuntimeRoot
$env:SAP_RPA_API_PREFIX = "http://127.0.0.1:8080/"

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
    $health = Invoke-RestMethod "http://127.0.0.1:8080/api/health" -TimeoutSec 5
    Write-Ok "API started: http://127.0.0.1:8080/api/health"
    $health | ConvertTo-Json -Depth 6
} catch {
    Write-Warn "API has not responded yet. Run the status check later. Error: $($_.Exception.Message)"
}
