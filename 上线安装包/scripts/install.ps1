param(
    [string]$PackageRoot = ""
)

$ErrorActionPreference = "Stop"

$packageRoot = if ($PackageRoot) { (Resolve-Path $PackageRoot).Path } elseif ($env:SAP_RPA_PACKAGE_SOURCE) { (Resolve-Path $env:SAP_RPA_PACKAGE_SOURCE).Path } else { Split-Path -Parent $PSScriptRoot }
$sourceDir = Join-Path $packageRoot "SapWebLauncher"
$sourceExe = Join-Path $sourceDir "SapWebLauncher.exe"
$installDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"
$installExe = Join-Path $installDir "SapWebLauncher.exe"

function Write-Step {
    param([string]$Message)
    Write-Host "[SAP RPA] $Message" -ForegroundColor Cyan
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

function Save-RegFile {
    param([string]$ExePath)

    $escaped = $ExePath.Replace("\", "\\")
    $regText = @"
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\sap-rpa]
@="URL:sap-rpa Protocol"
"URL Protocol"=""

[HKEY_CURRENT_USER\Software\Classes\sap-rpa\shell]

[HKEY_CURRENT_USER\Software\Classes\sap-rpa\shell\open]

[HKEY_CURRENT_USER\Software\Classes\sap-rpa\shell\open\command]
@="\"$escaped\" \"%1\""

[HKEY_CURRENT_USER\Software\Classes\sap-zck]
@="URL:sap-zck Protocol"
"URL Protocol"=""

[HKEY_CURRENT_USER\Software\Classes\sap-zck\shell]

[HKEY_CURRENT_USER\Software\Classes\sap-zck\shell\open]

[HKEY_CURRENT_USER\Software\Classes\sap-zck\shell\open\command]
@="\"$escaped\" \"%1\""
"@

    Set-Content -Path (Join-Path $packageRoot "register_sap_rpa_current_user.reg") -Value $regText -Encoding Unicode
}

if (-not (Test-Path $sourceExe)) {
    Write-Host "SapWebLauncher.exe not found in package: $sourceExe" -ForegroundColor Red
    Write-Host "Run scripts\make_package.ps1 on the build machine first." -ForegroundColor Yellow
    exit 1
}

Write-Step "Copy launcher to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir "*") -Destination $installDir -Recurse -Force

Write-Step "Register browser protocols for current user"
Register-Protocol -Protocol "sap-rpa" -ExePath $installExe
Register-Protocol -Protocol "sap-zck" -ExePath $installExe
Save-RegFile -ExePath $installExe

Write-Step "Run launcher self-test"
& $installExe test
if ($LASTEXITCODE -ne 0) {
    Write-Host "Launcher self-test failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Install completed." -ForegroundColor Green
Write-Host "Installed exe: $installExe"
Write-Host "Test URL: sap-rpa://run?action=run&tcode=ZFI019NL&script=openOnly&system=Fiori&client=400&user=UI5035&pw=fiori666&sysnr=04&lang=ZH"
Write-Host ""
Write-Host "Next step: run 02_检测环境.bat"
