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
$publishDir = Join-Path $OutputRoot "bin"
$scriptsDir = Join-Path $OutputRoot "scripts"
$docsDir = Join-Path $OutputRoot "docs"
$toolsDir = Join-Path $OutputRoot "tools"
$bundledRuntimeDir = Join-Path $OutputRoot ".dotnet"
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
New-Item -ItemType Directory -Force -Path $docsDir | Out-Null
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
New-Item -ItemType Directory -Force -Path $bundledRuntimeDir | Out-Null

Write-Host "Publishing SapWebLauncher with bundled .NET runtime..." -ForegroundColor Cyan
Write-Host "Restoring packages from available local cache (unreachable feeds are ignored)..." -ForegroundColor Cyan
& $DotnetPath restore $project `
    --ignore-failed-sources

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed. Ensure the NuGet cache is complete or temporarily allow access to the configured package source."
}

& $DotnetPath publish $project `
    -c Release `
    --no-restore `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$dotnetRoot = Split-Path -Parent $DotnetPath
foreach ($runtimeItem in @("dotnet.exe", "host", "shared")) {
    $runtimeSource = Join-Path $dotnetRoot $runtimeItem
    if (-not (Test-Path $runtimeSource)) {
        throw "Bundled .NET runtime item not found: $runtimeSource"
    }
    Copy-Item -LiteralPath $runtimeSource -Destination $bundledRuntimeDir -Recurse -Force
}

$packageRoot = Join-Path $repoRoot $nameOnlinePackage
Copy-Item -Path (Join-Path $packageRoot ("README_" + $nameInstallSteps + ".md")) -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot ($nameInstallDocList + ".md")) -Destination $OutputRoot -Force
foreach ($packageFile in @("生产部署拷贝清单.md", "04_切换SAP系统.bat", "00_生成上线安装包.cmd")) {
    $packageFilePath = Join-Path $packageRoot $packageFile
    if (Test-Path $packageFilePath) {
        Copy-Item -LiteralPath $packageFilePath -Destination $OutputRoot -Force
    }
}
Copy-Item -Path (Join-Path $packageRoot "config.local.example.json") -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot ("04_" + $nameConfigSapLogin + ".bat")) -Destination $OutputRoot -Force
Copy-Item -Path (Join-Path $packageRoot "scripts\configure_sap_login.ps1") -Destination $scriptsDir -Force
Copy-Item -Path (Join-Path $packageRoot "scripts\make_package.ps1") -Destination $scriptsDir -Force
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
    $transactionConfigPath = Join-Path $transactionsSource "transaction-config.json"
    if (-not (Test-Path $transactionConfigPath)) {
        throw "transaction-config.json not found: $transactionConfigPath"
    }

    $transactionConfig = Get-Content -LiteralPath $transactionConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $scriptTransactions = @($transactionConfig.transactions | Where-Object { $_.automation -eq "script" })
    $missingTransactionScripts = @(
        $scriptTransactions |
            Where-Object { -not $_.script -or -not (Test-Path (Join-Path $transactionsSource $_.script)) } |
            ForEach-Object { "{0} -> {1}" -f $_.code, $_.script }
    )
    if ($missingTransactionScripts.Count -gt 0) {
        throw "Executable transaction VBS missing: $($missingTransactionScripts -join ', ')"
    }

    Copy-Item -Path $transactionsSource -Destination $OutputRoot -Recurse -Force
    Write-Host ("Validated executable transaction scripts: {0}" -f $scriptTransactions.Count) -ForegroundColor Green
}

$scheduleAdminSource = Join-Path (Join-Path $repoRoot "tools") "schedule-admin"
if (Test-Path $scheduleAdminSource) {
    $scheduleAdminTarget = Join-Path $toolsDir "schedule-admin"
    Get-ChildItem -LiteralPath $scheduleAdminSource -Recurse -File |
        Where-Object { $_.DirectoryName -notmatch "(__pycache__|build|dist)" -and $_.Extension -ne ".pyc" } |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($scheduleAdminSource, $_.FullName)
            $targetPath = Join-Path $scheduleAdminTarget $relativePath
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetPath) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        }
}

$documentationFiles = @(
    (Join-Path $repoRoot "README.md"),
    (Join-Path $repoRoot "SapRpa_V2_功能说明书.html"),
    (Join-Path $repoRoot "反思.md")
)
foreach ($documentationFile in $documentationFiles) {
    if (Test-Path $documentationFile) {
        Copy-Item -LiteralPath $documentationFile -Destination $docsDir -Force
    }
}
$repositoryDocs = Join-Path $repoRoot "docs"
if (Test-Path $repositoryDocs) {
    Copy-Item -Path (Join-Path $repositoryDocs "*") -Destination $docsDir -Recurse -Force
}

Remove-Item -LiteralPath (Join-Path $publishDir "SapWebLauncher.pdb") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $OutputRoot "AI_HANDOFF_NEXT.md") -Force -ErrorAction SilentlyContinue

$version = @(
    "GeneratedAt=$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "RepoRoot=$repoRoot",
    "Project=$project",
    "PackageMode=framework-dependent-with-bundled-runtime",
    "DotnetRuntime=$bundledRuntimeDir",
    "LauncherExe=$(Join-Path $publishDir 'SapWebLauncher.exe')"
) -join [Environment]::NewLine
Set-Content -Path (Join-Path $OutputRoot "PACKAGE_VERSION.txt") -Value $version -Encoding UTF8

$manifestPath = Join-Path $OutputRoot "PACKAGE_MANIFEST.json"
$manifestFiles = @(
    Get-ChildItem -LiteralPath $OutputRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                path = [System.IO.Path]::GetRelativePath($OutputRoot, $_.FullName).Replace("\", "/")
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
[pscustomobject]@{
    generatedAt = (Get-Date).ToString("o")
    fileCount = $manifestFiles.Count
    files = $manifestFiles
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$zipPath = "$OutputRoot.zip"
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $OutputRoot -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Package generated: $OutputRoot" -ForegroundColor Green
Write-Host "Package zip: $zipPath" -ForegroundColor Green
Write-Host "Send this whole folder to target computers."
