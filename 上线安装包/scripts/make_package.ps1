param(
    [string]$PackageSource = "",
    [string]$OutputRoot = "",
    [string]$DotnetPath = ""
)

$ErrorActionPreference = "Stop"

function ConvertFrom-CodePoint {
    param([int[]]$CodePoints)
    return -join ($CodePoints | ForEach-Object { [char]$_ })
}

$nameInstallPackage = ConvertFrom-CodePoint @(0x5B89, 0x88C5, 0x5305)
$nameOnlinePackage = ConvertFrom-CodePoint @(0x4E0A, 0x7EBF, 0x5B89, 0x88C5, 0x5305)
$nameWebLogin = ConvertFrom-CodePoint @(0x7F51, 0x9875, 0x542F, 0x52A8, 0x767B, 0x5F55)
$nameInstallSteps = ConvertFrom-CodePoint @(0x5B89, 0x88C5, 0x6B65, 0x9AA4)
$nameInstallDocList = ConvertFrom-CodePoint @(0x4E0A, 0x7EBF, 0x5B89, 0x88C5, 0x6587, 0x6863, 0x6E05, 0x5355)
$nameConfigSapLogin = (ConvertFrom-CodePoint @(0x914D, 0x7F6E)) + "SAP" + (ConvertFrom-CodePoint @(0x767B, 0x5F55, 0x4FE1, 0x606F))
$nameStartupScripts = ConvertFrom-CodePoint @(0x542F, 0x52A8, 0x811A, 0x672C)
$nameFinalDeploySteps = ConvertFrom-CodePoint @(0x6700, 0x7EC8, 0x4E0A, 0x7EBF, 0x90E8, 0x7F72, 0x6B65, 0x9AA4)

if (-not $OutputRoot) {
    $OutputRoot = Join-Path "D:\RPA" $nameInstallPackage
}

$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { "" }
$packageSource = if ($PackageSource) { (Resolve-Path $PackageSource).Path } elseif ($env:SAP_RPA_PACKAGE_SOURCE) { (Resolve-Path $env:SAP_RPA_PACKAGE_SOURCE).Path } elseif ($scriptRootPath) { Split-Path -Parent $scriptRootPath } else { Join-Path "D:\RPA\RpaProject" $nameOnlinePackage }
$repoRootCandidate = Resolve-Path (Join-Path $packageSource "..")
if (-not (Test-Path (Join-Path $repoRootCandidate (Join-Path $nameWebLogin "SapWebLauncher\SapWebLauncher.csproj")))) {
    $repoRootCandidate = Resolve-Path "D:\RPA\RpaProject"
    $packageSource = Join-Path $repoRootCandidate $nameOnlinePackage
}
$repoRoot = $repoRootCandidate
$project = Join-Path $repoRoot (Join-Path $nameWebLogin "SapWebLauncher\SapWebLauncher.csproj")
$publishDir = Join-Path $OutputRoot "SapWebLauncher"
$scriptsDir = Join-Path $OutputRoot "scripts"
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

$outputFullPath = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd("\")
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd("\")
$packageFullPath = [System.IO.Path]::GetFullPath($packageSource).TrimEnd("\")
if ($outputFullPath -ieq [System.IO.Path]::GetPathRoot($outputFullPath).TrimEnd("\") -or
    $outputFullPath -ieq $repoFullPath -or
    $outputFullPath -ieq $packageFullPath -or
    $outputFullPath -ieq "D:\RPA") {
    throw "Refusing to clean unsafe OutputRoot: $OutputRoot"
}

Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null

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

$packageRoot = Join-Path $repoRoot $nameOnlinePackage
Copy-Item -Path (Join-Path $packageRoot ("README_" + $nameInstallSteps + ".md")) -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot ($nameInstallDocList + ".md")) -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot "config.local.example.json") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot ("04_" + $nameConfigSapLogin + ".bat")) -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot "scripts\configure_sap_login.ps1") -Destination $scriptsDir -Force
Copy-Item -Path (Join-Path $repoRoot "index.html") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $repoRoot "assets") -Destination $OutputRoot -Recurse -Force
$gatewaySource = Join-Path $repoRoot "gateway"
if (Test-Path $gatewaySource) {
    Copy-Item -Path $gatewaySource -Destination $OutputRoot -Recurse -Force
}
$startupSource = Join-Path $repoRoot $nameStartupScripts
if (Test-Path $startupSource) {
    Copy-Item -Path $startupSource -Destination $OutputRoot -Recurse -Force
}
$finalDeployDocs = Join-Path $packageSource $nameFinalDeploySteps
if (Test-Path $finalDeployDocs) {
    Copy-Item -Path $finalDeployDocs -Destination $OutputRoot -Recurse -Force
}
$transactionsSource = Join-Path $repoRoot (Join-Path $nameWebLogin "transactions")
if (Test-Path $transactionsSource) {
    Copy-Item -Path $transactionsSource -Destination $OutputRoot -Recurse -Force
}

Remove-Item -LiteralPath (Join-Path $publishDir "SapWebLauncher.pdb") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $OutputRoot "AI_HANDOFF_NEXT.md") -Force -ErrorAction SilentlyContinue

$version = @(
    "GeneratedAt=$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "RepoRoot=$repoRoot",
    "Project=$project",
    "LauncherExe=$(Join-Path $publishDir 'SapWebLauncher.exe')"
) -join [Environment]::NewLine
Set-Content -Path (Join-Path $OutputRoot "PACKAGE_VERSION.txt") -Value $version -Encoding UTF8

Write-Host ""
Write-Host "Package generated: $OutputRoot" -ForegroundColor Green
Write-Host "Send this whole folder to target computers."
