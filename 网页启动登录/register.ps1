# Register SapWebLauncher browser protocols for the current Windows user.
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\register.ps1
# It registers both sap-rpa:// and the legacy sap-zck:// scheme.

$ErrorActionPreference = "Stop"

$releasePath = Join-Path $PSScriptRoot "SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe"
$debugPath = Join-Path $PSScriptRoot "SapWebLauncher\bin\Debug\net8.0-windows\SapWebLauncher.exe"
$fallbackReleasePath = Join-Path $PSScriptRoot "SapWebLauncher\bin\x64\Release\net8.0-windows\SapWebLauncher.exe"
$fallbackDebugPath = Join-Path $PSScriptRoot "SapWebLauncher\bin\x64\Debug\net8.0-windows\SapWebLauncher.exe"

$appPath = @($releasePath, $debugPath, $fallbackReleasePath, $fallbackDebugPath) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $appPath) {
    Write-Host "SapWebLauncher.exe not found. Build first:" -ForegroundColor Red
    Write-Host "  dotnet build `"$PSScriptRoot\SapWebLauncher\SapWebLauncher.csproj`" -c Release" -ForegroundColor Gray
    exit 1
}

function Register-Protocol {
    param(
        [Parameter(Mandatory = $true)][string]$Protocol,
        [Parameter(Mandatory = $true)][string]$ExePath
    )

    $root = "HKCU:\Software\Classes\$Protocol"
    Remove-Item $root -Force -Recurse -ErrorAction SilentlyContinue
    New-Item $root -Force | Out-Null
    Set-ItemProperty $root -Name "(default)" -Value "URL:$Protocol Protocol"
    Set-ItemProperty $root -Name "URL Protocol" -Value ""
    New-Item "$root\shell\open\command" -Force | Out-Null
    Set-ItemProperty "$root\shell\open\command" -Name "(default)" -Value "`"$ExePath`" `"%1`""
}

Register-Protocol -Protocol "sap-rpa" -ExePath $appPath
Register-Protocol -Protocol "sap-zck" -ExePath $appPath

Write-Host "OK: sap-rpa:// and sap-zck:// have been registered." -ForegroundColor Green
Write-Host "Executable: $appPath" -ForegroundColor Gray
Write-Host ""
Write-Host "Test link:" -ForegroundColor Cyan
Write-Host "sap-rpa://run?action=run&tcode=ZFI019NL&system=Fiori&client=400&user=UI5035&pw=fiori666&sysnr=04"
