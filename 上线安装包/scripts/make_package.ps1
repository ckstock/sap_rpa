param(
    [string]$PackageSource = "",
    [string]$OutputRoot = "D:\工作\SapRpa上线安装包",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"

$packageSource = if ($PackageSource) { (Resolve-Path $PackageSource).Path } elseif ($env:SAP_RPA_PACKAGE_SOURCE) { (Resolve-Path $env:SAP_RPA_PACKAGE_SOURCE).Path } else { Split-Path -Parent $PSScriptRoot }
$repoRoot = Resolve-Path (Join-Path $packageSource "..")
$project = Join-Path $repoRoot "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"
$publishDir = Join-Path $OutputRoot "SapWebLauncher"

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

Copy-Item -Path (Join-Path $packageSource "README_安装步骤.md") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "01_安装到本机.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "02_检测环境.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "03_卸载协议和程序.bat") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageSource "scripts") -Destination $OutputRoot -Recurse -Force

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
