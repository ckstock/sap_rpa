param(
    [string]$PackageRoot = ""
)

$ErrorActionPreference = "Continue"

$installDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"
$installExe = Join-Path $installDir "SapWebLauncher.exe"

function Get-ProtocolCommand {
    param([string]$Protocol)
    $path = "HKCU:\Software\Classes\$Protocol\shell\open\command"
    if (-not (Test-Path $path)) {
        return $null
    }

    return (Get-Item $path).GetValue("")
}

function Find-SapShortcut {
    $programFiles = [Environment]::GetFolderPath("ProgramFiles")
    $programFilesX86 = [Environment]::GetFolderPath("ProgramFilesX86")
    $candidates = @(
        (Join-Path $programFilesX86 "SAP\FrontEnd\SAPgui\sapshcut.exe"),
        (Join-Path $programFiles "SAP\FrontEnd\SAPgui\sapshcut.exe"),
        "C:\SAP\FrontEnd\SAPgui\sapshcut.exe",
        "C:\software\SAPgui\sapshcut.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $cmd = Get-Command sapshcut.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    return $null
}

Write-Host "=== SAP RPA local environment check ===" -ForegroundColor Cyan

if (Test-Path $installExe) {
    Write-Host "[OK] Launcher installed: $installExe" -ForegroundColor Green
    & $installExe test
} else {
    Write-Host "[FAIL] Launcher is not installed: $installExe" -ForegroundColor Red
}

foreach ($protocol in @("sap-rpa", "sap-zck")) {
    $command = Get-ProtocolCommand -Protocol $protocol
    if ($command) {
        Write-Host "[OK] $protocol protocol: $command" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $protocol protocol is not registered" -ForegroundColor Red
    }
}

$sapShortcut = Find-SapShortcut
if ($sapShortcut) {
    Write-Host "[OK] SAP GUI shortcut found: $sapShortcut" -ForegroundColor Green
} else {
    Write-Host "[WARN] sapshcut.exe not found. Install SAP GUI before running SAP scripts." -ForegroundColor Yellow
}

$scriptingKey = "HKCU:\Software\SAP\SAPGUI Front\SAP Frontend Server\Security"
if (Test-Path $scriptingKey) {
    $security = Get-ItemProperty $scriptingKey
    Write-Host "[INFO] SAP GUI security registry found: $scriptingKey" -ForegroundColor Gray
    if ($null -ne $security.UserScripting) {
        Write-Host "[INFO] UserScripting value: $($security.UserScripting)" -ForegroundColor Gray
    }
} else {
    Write-Host "[INFO] SAP GUI scripting registry key not found. This may be normal before SAP GUI first run." -ForegroundColor Gray
}

Write-Host ""
Write-Host "If protocol and launcher are OK, open the Netlify portal and click the launcher button." -ForegroundColor Cyan
