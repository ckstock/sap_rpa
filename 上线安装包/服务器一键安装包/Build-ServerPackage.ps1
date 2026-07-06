param(
    [string]$RepoRoot = "",
    [string]$OutputRoot = "",
    [string]$DotnetPath = "",
    [string]$PackageName = ""
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param([string]$PathText)
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($PathText))
}

$setupRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} elseif (-not [string]::IsNullOrWhiteSpace($env:SAP_RPA_PACKAGE_SETUP_DIR)) {
    Resolve-FullPath $env:SAP_RPA_PACKAGE_SETUP_DIR
} else {
    (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Resolve-FullPath (Join-Path $setupRoot "..\..")
} else {
    $RepoRoot = Resolve-FullPath $RepoRoot
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "dist"
} else {
    $OutputRoot = Resolve-FullPath $OutputRoot
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = "SapRpaV2_WindowsServer_8080"
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $bundled = "D:\sap_ai\.dotnet\dotnet.exe"
    if (Test-Path $bundled) {
        $DotnetPath = $bundled
    } else {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) { $DotnetPath = $cmd.Source }
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path $DotnetPath)) {
    throw "dotnet SDK not found. Pass -DotnetPath or install .NET 8 SDK."
}

$project = Join-Path $RepoRoot "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"
$index = Join-Path $RepoRoot "index.html"
$assets = Join-Path $RepoRoot "assets"
$transactions = Join-Path $RepoRoot "网页启动登录\transactions"

foreach ($required in @($project, $index, $assets, $transactions)) {
    if (-not (Test-Path $required)) { throw "Required package source missing: $required" }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$stageRoot = Join-Path $OutputRoot $PackageName
$zipPath = Join-Path $OutputRoot ($PackageName + ".zip")
$publishDir = Join-Path $stageRoot "bin"

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageRoot,$publishDir | Out-Null

Write-Host "[build] publishing SapWebLauncher to $publishDir"
& $DotnetPath publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

Copy-Item -LiteralPath $index -Destination (Join-Path $stageRoot "index.html") -Force
Copy-Item -LiteralPath $assets -Destination (Join-Path $stageRoot "assets") -Recurse -Force
Copy-Item -LiteralPath $transactions -Destination (Join-Path $stageRoot "transactions") -Recurse -Force

foreach ($fileName in @(
    "启动一键安装器.bat",
    "GitHub拉取并一键安装.bat",
    "SapRpaServerSetup.ps1",
    "README_服务器一键安装说明.md",
    "关键功能说明.md",
    "config.local.example.json"
)) {
    $source = Join-Path $setupRoot $fileName
    if (-not (Test-Path $source)) { throw "Required setup file missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stageRoot $fileName) -Force
}

foreach ($doc in @(
    "SapRpa_V2_功能说明书.html",
    "DATABASE_FIELD_DESIGN.md",
    "AI_HANDOFF_NEXT.md",
    "对方平台Token跳转调用说明.md"
)) {
    $source = Join-Path $RepoRoot $doc
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $stageRoot $doc) -Force
    }
}

$version = [ordered]@{
    package = $PackageName
    generatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    repoRoot = $RepoRoot
    branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
    commit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
    apiBaseUrl = "http://127.0.0.1:8080"
    launcher = "bin\SapWebLauncher.exe"
    installEntry = "启动一键安装器.bat"
    runtimeDefault = "D:\SAP_RPA"
    persistencePolicy = "Keeps data, logs, outputs, config.local.json, and SAP DPAPI login config during upgrade."
}
$version | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stageRoot "PACKAGE_VERSION.json") -Encoding UTF8

$forbiddenNames = @(
    "config.local.json",
    ".env",
    ".netlify_ticket.json",
    ".netlify_auth_url.txt",
    "sap-rpa-config.db",
    "launcher.log"
)
$forbiddenFiles = Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Force |
    Where-Object {
        $leaf = $_.Name.ToLowerInvariant()
        $full = $_.FullName.ToLowerInvariant()
        ($forbiddenNames | ForEach-Object { $leaf -eq $_ }) -contains $true -or
        $full -match '\\data\\|\\logs\\|\\outputs\\|\\.git\\|\\.gitnexus\\|\\.claude\\'
    }
if ($forbiddenFiles) {
    $list = ($forbiddenFiles | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
    throw "Forbidden files found in package stage:$([Environment]::NewLine)$list"
}

Write-Host "[build] compressing $zipPath"
Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -Force

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
Write-Host "[build] package: $zipPath"
Write-Host "[build] sha256: $($hash.Hash)"
