param(
    [string]$PackageRoot = ""
)

$ErrorActionPreference = "Stop"

$packageRoot = if ($PackageRoot) { (Resolve-Path $PackageRoot).Path } elseif ($env:SAP_RPA_PACKAGE_SOURCE) { (Resolve-Path $env:SAP_RPA_PACKAGE_SOURCE).Path } else { Split-Path -Parent $PSScriptRoot }
$sourceDir = Join-Path $packageRoot "SapWebLauncher"
$sourceExe = Join-Path $sourceDir "SapWebLauncher.exe"
$installDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"
$installExe = Join-Path $installDir "SapWebLauncher.exe"
$configDir = Join-Path $env:LOCALAPPDATA "SapWebLauncher"
$configFile = Join-Path $configDir "config.json"

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
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\$Protocol")
    $key.SetValue("", "URL:$Protocol Protocol")
    $key.SetValue("URL Protocol", "")
    $cmdKey = $key.CreateSubKey("shell\open\command")
    $cmdKey.SetValue("", "`"$ExePath`" `"%1`"")
    $cmdKey.Close()
    $key.Close()
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
$transactionsDir = Join-Path $packageRoot "transactions"
if (Test-Path $transactionsDir) {
    Write-Step "Copy transaction scripts to local launcher"
    Copy-Item -Path $transactionsDir -Destination $installDir -Recurse -Force
}

Write-Step "Create local SAP config template"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
if (-not (Test-Path $configFile)) {
    $config = [ordered]@{
        system = ""
        client = ""
        user = ""
        passwordProtected = ""
        language = "ZH"
        sysNr = ""
    } | ConvertTo-Json
    Set-Content -LiteralPath $configFile -Value $config -Encoding UTF8
}

Write-Step "Register browser protocol for current user"
Register-Protocol -Protocol "sap-rpa" -ExePath $installExe
Save-RegFile -ExePath $installExe

Write-Step "Run launcher self-test"
$testProcess = Start-Process -FilePath $installExe -ArgumentList "test" -Wait -PassThru -NoNewWindow
if ($testProcess.ExitCode -ne 0) {
    Write-Host "Launcher self-test failed." -ForegroundColor Red
    exit $testProcess.ExitCode
}

Write-Host ""
Write-Host "Install completed." -ForegroundColor Green
Write-Host "Installed exe: $installExe"
Write-Host "Config file: $configFile"
Write-Host "Test URL: sap-rpa://run?action=run&tcode=ZFI019NL&script=openOnly"
Write-Host ""
Write-Host "Next step: run 04_配置SAP登录信息.bat, then 02_检测环境.bat"
