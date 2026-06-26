$ErrorActionPreference = "Stop"

$RuntimeRoot = if ($env:SAP_RPA_HOME) { $env:SAP_RPA_HOME } else { "D:\sap_ai" }
$RepoRoot = "D:\工作\sap_rpa"
$LauncherProject = Join-Path $RepoRoot "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"
$SourceLauncherBin = Join-Path $RepoRoot "网页启动登录\SapWebLauncher\bin\Release\net8.0-windows"
$SourceTransactions = Join-Path $RepoRoot "网页启动登录\transactions"
$RuntimeTransactions = Join-Path $RuntimeRoot "transactions"
$RuntimeData = Join-Path $RuntimeRoot "data"
$RuntimeLogs = Join-Path $RuntimeRoot "logs"
$RuntimeOutputs = Join-Path $RuntimeRoot "outputs"
$RuntimeBin = Join-Path $RuntimeRoot "bin"
$RuntimeIndex = Join-Path $RuntimeRoot "index.html"
$RuntimeAssets = Join-Path $RuntimeRoot "assets"
$InstalledLauncherDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"
$InstalledLauncherExe = Join-Path $InstalledLauncherDir "SapWebLauncher.exe"
$RuntimeLauncherExe = Join-Path $RuntimeBin "SapWebLauncher.exe"
$DotnetPath = Join-Path $env:LOCALAPPDATA "CodexDotnetSdk8_421\dotnet.exe"

function Write-Step {
    param([string]$Message)
    Write-Host "[SAP RPA V2] $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Ensure-RuntimeDirs {
    New-Item -ItemType Directory -Force -Path $RuntimeRoot,$RuntimeTransactions,$RuntimeData,$RuntimeLogs,$RuntimeOutputs,$RuntimeBin,$RuntimeAssets | Out-Null
}

function Get-LauncherExe {
    if (Test-Path $RuntimeLauncherExe) { return $RuntimeLauncherExe }
    if (Test-Path $InstalledLauncherExe) { return $InstalledLauncherExe }
    throw "SapWebLauncher.exe not found. Run the launcher installation step first."
}

function Register-SapRpaProtocol {
    param([Parameter(Mandatory = $true)][string]$ExePath)

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\sap-rpa")
    $key.SetValue("", "URL:sap-rpa Protocol")
    $key.SetValue("URL Protocol", "")
    $cmdKey = $key.CreateSubKey("shell\open\command")
    $cmdKey.SetValue("", "`"$ExePath`" `"%1`"")
    $cmdKey.Close()
    $key.Close()
}
