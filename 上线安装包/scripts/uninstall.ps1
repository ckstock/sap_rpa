param(
    [string]$PackageRoot = "",
    [switch]$RemoveFiles
)

$ErrorActionPreference = "Continue"
$installDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"

foreach ($protocol in @("sap-rpa", "sap-zck")) {
    $root = "HKCU:\Software\Classes\$protocol"
    if (Test-Path $root) {
        Remove-Item $root -Force -Recurse
        Write-Host "[OK] Removed protocol: $protocol" -ForegroundColor Green
    } else {
        Write-Host "[INFO] Protocol not found: $protocol" -ForegroundColor Gray
    }
}

if ($RemoveFiles -and (Test-Path $installDir)) {
    Remove-Item $installDir -Force -Recurse
    Write-Host "[OK] Removed install folder: $installDir" -ForegroundColor Green
}

Write-Host "Uninstall completed." -ForegroundColor Green
