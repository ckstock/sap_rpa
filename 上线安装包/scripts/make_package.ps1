param(
    [string]$PackageSource = "",
    [string]$OutputRoot = "D:\工作\SapRpa上线安装包",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"

$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { "" }
$packageSource = if ($PackageSource) { (Resolve-Path $PackageSource).Path } elseif ($env:SAP_RPA_PACKAGE_SOURCE) { (Resolve-Path $env:SAP_RPA_PACKAGE_SOURCE).Path } elseif ($scriptRootPath) { Split-Path -Parent $scriptRootPath } else { "D:\工作\sap_rpa\上线安装包" }
$repoRootCandidate = Resolve-Path (Join-Path $packageSource "..")
if (-not (Test-Path (Join-Path $repoRootCandidate "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"))) {
    $repoRootCandidate = Resolve-Path "D:\工作\sap_rpa"
    $packageSource = Join-Path $repoRootCandidate "上线安装包"
}
$repoRoot = $repoRootCandidate
$project = Join-Path $repoRoot "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"
$publishDir = Join-Path $OutputRoot "SapWebLauncher"
$sourceEqualsOutput = $false
try {
    if ((Resolve-Path $packageSource).Path.TrimEnd("\") -ieq (Resolve-Path $OutputRoot -ErrorAction SilentlyContinue).Path.TrimEnd("\")) {
        $sourceEqualsOutput = $true
    }
} catch { }

if (-not $DotnetPath) {
    $bundled = Join-Path $env:LOCALAPPDATA "CodexDotnetSdk8_421\dotnet.exe"
    if (Test-Path $bundled) {
        $DotnetPath = $bundled
    } else {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) {
            $DotnetPath = $cmd.Source
        }
    }
}

if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) {
    throw "dotnet SDK not found. Install .NET 8 SDK or pass -DotnetPath."
}

Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing SapWebLauncher self-contained package..." -ForegroundColor Cyan
& $DotnetPath publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

if (-not $sourceEqualsOutput) {
    Copy-Item -Path (Join-Path $packageSource "README_安装步骤.md") -Destination $OutputRoot -Force
} else {
    Copy-Item -Path (Join-Path (Join-Path $repoRoot "上线安装包") "README_安装步骤.md") -Destination $OutputRoot -Force
}
Copy-Item -Path (Join-Path $packageSource "01_安装到本机.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "02_检测环境.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "03_卸载协议和程序.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "04_配置SAP登录信息.bat") -Destination $OutputRoot -Force
if (-not $sourceEqualsOutput) {
    Copy-Item -Path (Join-Path $packageSource "scripts") -Destination $OutputRoot -Recurse -Force
}

$version = @"
GeneratedAt=$((Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))
RepoRoot=$repoRoot
Project=$project
LauncherExe=$(Join-Path $publishDir "SapWebLauncher.exe")
"@
Set-Content -Path (Join-Path $OutputRoot "PACKAGE_VERSION.txt") -Value $version -Encoding UTF8

Write-Host ""
Write-Host "Package generated: $OutputRoot" -ForegroundColor Green
Write-Host "Send this whole folder to target computers."
