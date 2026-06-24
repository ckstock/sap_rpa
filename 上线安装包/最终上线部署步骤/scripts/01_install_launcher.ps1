$scriptRootPath = if ($PSScriptRoot) { $PSScriptRoot } elseif ($env:SAP_RPA_DEPLOY_SCRIPT_ROOT) { $env:SAP_RPA_DEPLOY_SCRIPT_ROOT } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptRootPath "common.ps1")

Ensure-RuntimeDirs

if (Test-Path $LauncherProject) {
    if (-not (Test-Path $DotnetPath)) {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCommand) { $DotnetPath = $dotnetCommand.Source }
    }
    if (-not (Test-Path $DotnetPath)) {
        throw ".NET 8 SDK not found. Install .NET 8 SDK or build SapWebLauncher on a packaging machine first."
    }

    Write-Step "Building SapWebLauncher"
    & $DotnetPath build $LauncherProject -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}

if (-not (Test-Path (Join-Path $SourceLauncherBin "SapWebLauncher.exe"))) {
    throw "SapWebLauncher.exe not found: $(Join-Path $SourceLauncherBin 'SapWebLauncher.exe')"
}

Write-Step "Copying launcher to runtime directory: $RuntimeBin"
Copy-Item -Path (Join-Path $SourceLauncherBin "*") -Destination $RuntimeBin -Recurse -Force

Write-Step "Installing launcher for current Windows user: $InstalledLauncherDir"
New-Item -ItemType Directory -Force -Path $InstalledLauncherDir | Out-Null
Copy-Item -Path (Join-Path $RuntimeBin "*") -Destination $InstalledLauncherDir -Recurse -Force

if (Test-Path $RuntimeTransactions) {
    Copy-Item -Path $RuntimeTransactions -Destination $InstalledLauncherDir -Recurse -Force
}

Write-Step "Registering sap-rpa:// protocol"
Register-SapRpaProtocol -ExePath $InstalledLauncherExe

Write-Step "Running launcher self-test"
& $InstalledLauncherExe test
if ($LASTEXITCODE -ne 0) { throw "SapWebLauncher test failed." }

Write-Ok "Launcher installation and protocol registration completed"
