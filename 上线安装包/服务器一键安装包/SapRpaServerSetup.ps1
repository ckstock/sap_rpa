param(
    [switch]$Cli,
    [ValidateSet("gui", "install", "check", "start-api", "stop-api", "open-page", "init-db", "backup", "reset-db", "configure-login")]
    [string]$Action = "gui",
    [string]$RuntimeRoot = "",
    [string]$SourceRoot = "",
    [int]$ApiPort = 17890,
    [switch]$ForceResetDb
)

$ErrorActionPreference = "Stop"

$script:SetupRoot = if ($env:SAP_RPA_SETUP_DIR) {
    $env:SAP_RPA_SETUP_DIR
} elseif ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    (Get-Location).Path
}

$script:LogFile = $null
$script:LogBox = $null
$script:StepList = $null
$script:State = [ordered]@{}
$script:ApiBaseUrl = "http://127.0.0.1:$ApiPort"
$script:RunningCliMode = $false

function Get-DefaultRuntimeRoot {
    if (-not [string]::IsNullOrWhiteSpace($RuntimeRoot)) { return $RuntimeRoot }
    if (-not [string]::IsNullOrWhiteSpace($env:SAP_RPA_HOME)) { return $env:SAP_RPA_HOME }
    return "C:\SAP_RPA"
}

function Get-DefaultSourceRoot {
    if (-not [string]::IsNullOrWhiteSpace($SourceRoot)) { return $SourceRoot }
    if (-not [string]::IsNullOrWhiteSpace($env:SAP_RPA_SOURCE_ROOT)) { return $env:SAP_RPA_SOURCE_ROOT }

    $parent = Split-Path -Parent $script:SetupRoot
    $grandParent = if ($parent) { Split-Path -Parent $parent } else { "" }
    $candidates = @($grandParent, $parent, $script:SetupRoot)

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }
    return $script:SetupRoot
}

function Normalize-PathText {
    param([string]$PathText)
    if ([string]::IsNullOrWhiteSpace($PathText)) { return "" }
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($PathText.Trim().Trim('"')))
}

function Initialize-Log {
    param([string]$Root)
    $logDir = Join-Path $Root "logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $script:LogFile = Join-Path $logDir "installer-gui.log"
    Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value ""
    Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value ("===== SAP RPA V2 installer {0} =====" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
}

function Write-SetupLog {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    Write-Host $line
    if ($script:LogFile) {
        try { Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value $line } catch { }
    }
    if ($script:LogBox) {
        $script:LogBox.AppendText($line + [Environment]::NewLine)
        $script:LogBox.SelectionStart = $script:LogBox.TextLength
        $script:LogBox.ScrollToCaret()
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Set-StepStatus {
    param(
        [string]$Step,
        [string]$Status,
        [string]$Message = ""
    )

    $script:State[$Step] = [ordered]@{
        status = $Status
        message = $Message
        time = (Get-Date).ToString("s")
    }

    if ($script:StepList) {
        foreach ($item in $script:StepList.Items) {
            if ($item.Text -eq $Step) {
                $item.SubItems[1].Text = $Status
                $item.SubItems[2].Text = $Message
                break
            }
        }
        [System.Windows.Forms.Application]::DoEvents()
    }

    if ($script:LogFile) {
        $stateFile = Join-Path (Split-Path -Parent $script:LogFile) "installer-state.json"
        $script:State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $stateFile -Encoding UTF8
    }
}

function Get-InstallPaths {
    param(
        [string]$Runtime,
        [string]$Source
    )

    $runtimeFull = Normalize-PathText $Runtime
    $sourceFull = Normalize-PathText $Source
    if ([string]::IsNullOrWhiteSpace($runtimeFull)) { throw "运行根目录不能为空。" }
    if ([string]::IsNullOrWhiteSpace($sourceFull)) { throw "源码/发布包根目录不能为空。" }

    return [ordered]@{
        RuntimeRoot = $runtimeFull
        SourceRoot = $sourceFull
        RuntimeBin = Join-Path $runtimeFull "bin"
        RuntimeData = Join-Path $runtimeFull "data"
        RuntimeLogs = Join-Path $runtimeFull "logs"
        RuntimeOutputs = Join-Path $runtimeFull "outputs"
        RuntimeTransactions = Join-Path $runtimeFull "transactions"
        RuntimeIndex = Join-Path $runtimeFull "index.html"
        RuntimeDb = Join-Path (Join-Path $runtimeFull "data") "sap-rpa-config.db"
        RuntimeLauncherExe = Join-Path (Join-Path $runtimeFull "bin") "SapWebLauncher.exe"
        InstalledLauncherDir = Join-Path $env:LOCALAPPDATA "SapRpaLauncher"
        InstalledLauncherExe = Join-Path (Join-Path $env:LOCALAPPDATA "SapRpaLauncher") "SapWebLauncher.exe"
        SapLoginConfigDir = Join-Path $env:LOCALAPPDATA "SapWebLauncher"
        SapLoginConfigFile = Join-Path (Join-Path $env:LOCALAPPDATA "SapWebLauncher") "config.json"
    }
}

function Ensure-RuntimeDirs {
    param([hashtable]$Paths)
    foreach ($path in @(
        $Paths.RuntimeRoot,
        $Paths.RuntimeBin,
        $Paths.RuntimeData,
        $Paths.RuntimeLogs,
        $Paths.RuntimeOutputs,
        $Paths.RuntimeTransactions
    )) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function Test-SamePath {
    param([string]$A, [string]$B)
    if ([string]::IsNullOrWhiteSpace($A) -or [string]::IsNullOrWhiteSpace($B)) { return $false }
    try {
        return ([IO.Path]::GetFullPath($A).TrimEnd('\') -ieq [IO.Path]::GetFullPath($B).TrimEnd('\'))
    } catch {
        return $false
    }
}

function Get-FirstExistingPath {
    param([string[]]$Candidates)
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return (Normalize-PathText $candidate)
        }
    }
    return ""
}

function Resolve-SourceIndex {
    param([hashtable]$Paths)
    return Get-FirstExistingPath @(
        (Join-Path $Paths.SourceRoot "index.html"),
        (Join-Path $Paths.SourceRoot "clean-code\index.html")
    )
}

function Resolve-SourceTransactions {
    param([hashtable]$Paths)
    return Get-FirstExistingPath @(
        (Join-Path $Paths.SourceRoot "网页启动登录\transactions"),
        (Join-Path $Paths.SourceRoot "transactions")
    )
}

function Resolve-SourceLauncherBin {
    param([hashtable]$Paths)
    return Get-FirstExistingPath @(
        (Join-Path $Paths.SourceRoot "bin"),
        (Join-Path $Paths.SourceRoot "网页启动登录\SapWebLauncher\bin\Release\net8.0-windows"),
        (Join-Path $Paths.SourceRoot "网页启动登录\SapWebLauncher\bin\Release\net8.0-windows\publish")
    )
}

function Resolve-LauncherProject {
    param([hashtable]$Paths)
    return Get-FirstExistingPath @(
        (Join-Path $Paths.SourceRoot "网页启动登录\SapWebLauncher\SapWebLauncher.csproj"),
        (Join-Path $Paths.SourceRoot "SapWebLauncher\SapWebLauncher.csproj")
    )
}

function Find-Dotnet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT "dotnet.exe") }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "dotnet\dotnet.exe") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe") }
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

function Copy-DirectoryContents {
    param(
        [string]$Source,
        [string]$Destination
    )
    if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path $Source)) {
        throw "源目录不存在：$Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if (Test-SamePath $Source $Destination) {
        Write-SetupLog "源目录和目标目录相同，跳过复制：$Source" "WARN"
        return
    }
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function New-RuntimeBackup {
    param([hashtable]$Paths)

    Ensure-RuntimeDirs $Paths
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupRoot = Join-Path (Join-Path $Paths.RuntimeRoot "backups") "backup-$stamp"
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

    $items = @(
        $Paths.RuntimeIndex,
        $Paths.RuntimeBin,
        $Paths.RuntimeTransactions,
        $Paths.RuntimeDb,
        (Join-Path $Paths.RuntimeRoot "config.local.json"),
        (Join-Path $Paths.RuntimeRoot "config.local.example.json")
    )
    $copied = 0
    foreach ($item in $items) {
        if (Test-Path $item) {
            $target = Join-Path $backupRoot (Split-Path -Leaf $item)
            Copy-Item -LiteralPath $item -Destination $target -Recurse -Force
            $copied++
        }
    }

    Write-SetupLog "已备份当前运行目录关键文件：$backupRoot，文件/目录数：$copied"
    return $backupRoot
}

function Register-SapRpaProtocol {
    param([string]$ExePath)
    if (-not (Test-Path $ExePath)) { throw "执行器不存在，无法注册协议：$ExePath" }

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\sap-rpa")
    $key.SetValue("", "URL:sap-rpa Protocol")
    $key.SetValue("URL Protocol", "")
    $cmdKey = $key.CreateSubKey("shell\open\command")
    $cmdKey.SetValue("", "`"$ExePath`" `"%1`"")
    $cmdKey.Close()
    $key.Close()
    Write-SetupLog "已注册当前 Windows 用户的 sap-rpa:// 协议：$ExePath"
}

function Get-LauncherExe {
    param([hashtable]$Paths)
    if (Test-Path $Paths.InstalledLauncherExe) { return $Paths.InstalledLauncherExe }
    if (Test-Path $Paths.RuntimeLauncherExe) { return $Paths.RuntimeLauncherExe }
    throw "找不到 SapWebLauncher.exe，请先执行部署/升级。"
}

function Invoke-Launcher {
    param(
        [hashtable]$Paths,
        [string[]]$Arguments
    )
    $launcher = Get-LauncherExe $Paths
    $oldHome = $env:SAP_RPA_HOME
    $env:SAP_RPA_HOME = $Paths.RuntimeRoot
    $stdoutFile = Join-Path $Paths.RuntimeLogs ("launcher-stdout-{0}.log" -f ([guid]::NewGuid().ToString("N")))
    $stderrFile = Join-Path $Paths.RuntimeLogs ("launcher-stderr-{0}.log" -f ([guid]::NewGuid().ToString("N")))
    try {
        Write-SetupLog ("执行：{0} {1}" -f $launcher, ($Arguments -join " "))
        $proc = Start-Process -FilePath $launcher -ArgumentList $Arguments -WorkingDirectory (Split-Path -Parent $launcher) -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
        foreach ($file in @($stdoutFile, $stderrFile)) {
            if (Test-Path $file) {
                foreach ($line in (Get-Content -LiteralPath $file -Encoding Default -ErrorAction SilentlyContinue)) {
                    if (-not [string]::IsNullOrWhiteSpace($line)) { Write-SetupLog ([string]$line) }
                }
            }
        }
        if ($proc.ExitCode -ne 0) { throw "SapWebLauncher 返回错误码：$($proc.ExitCode)" }
    }
    finally {
        $env:SAP_RPA_HOME = $oldHome
        foreach ($file in @($stdoutFile, $stderrFile)) {
            if (Test-Path $file) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
        }
    }
}

function Install-OrUpgrade {
    param([hashtable]$Paths)

    Set-StepStatus "准备运行目录" "Running"
    Ensure-RuntimeDirs $Paths
    Set-StepStatus "准备运行目录" "Success" $Paths.RuntimeRoot

    Set-StepStatus "备份当前版本" "Running"
    New-RuntimeBackup $Paths | Out-Null
    Set-StepStatus "备份当前版本" "Success"

    Set-StepStatus "复制页面和 VBS" "Running"
    $sourceIndex = Resolve-SourceIndex $Paths
    if ($sourceIndex) {
        if (-not (Test-SamePath $sourceIndex $Paths.RuntimeIndex)) {
            Copy-Item -LiteralPath $sourceIndex -Destination $Paths.RuntimeIndex -Force
        }
        Write-SetupLog "已准备运行页面：$($Paths.RuntimeIndex)"
    } else {
        Write-SetupLog "未找到 index.html，跳过页面复制。" "WARN"
    }

    $sourceTransactions = Resolve-SourceTransactions $Paths
    if ($sourceTransactions) {
        Copy-DirectoryContents -Source $sourceTransactions -Destination $Paths.RuntimeTransactions
        Write-SetupLog "已准备 VBS 目录：$($Paths.RuntimeTransactions)"
    } else {
        Write-SetupLog "未找到 transactions 目录，跳过 VBS 复制。" "WARN"
    }

    $exampleConfig = Join-Path $script:SetupRoot "config.local.example.json"
    if (Test-Path $exampleConfig) {
        Copy-Item -LiteralPath $exampleConfig -Destination (Join-Path $Paths.RuntimeRoot "config.local.example.json") -Force
    }
    Set-StepStatus "复制页面和 VBS" "Success"

    Set-StepStatus "安装执行器" "Running"
    $sourceBin = Resolve-SourceLauncherBin $Paths
    if (-not $sourceBin -or -not (Test-Path (Join-Path $sourceBin "SapWebLauncher.exe"))) {
        $project = Resolve-LauncherProject $Paths
        if (-not $project) {
            throw "找不到已编译执行器，也找不到 SapWebLauncher.csproj。请把发布包或 Git 仓库根目录填到 [源码/发布包根目录]。"
        }
        $dotnet = Find-Dotnet
        if (-not $dotnet) {
            throw "服务器没有找到 dotnet。请安装 .NET 8 SDK，或使用已带 bin\SapWebLauncher.exe 的发布包。"
        }
        Write-SetupLog "未找到执行器发布目录，开始构建：$project"
        $buildOutput = & $dotnet build $project -c Release 2>&1
        foreach ($line in $buildOutput) { Write-SetupLog ([string]$line) }
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败：$LASTEXITCODE" }
        $sourceBin = Resolve-SourceLauncherBin $Paths
    }

    if (-not $sourceBin -or -not (Test-Path (Join-Path $sourceBin "SapWebLauncher.exe"))) {
        throw "构建后仍未找到 SapWebLauncher.exe。"
    }

    Copy-DirectoryContents -Source $sourceBin -Destination $Paths.RuntimeBin
    New-Item -ItemType Directory -Force -Path $Paths.InstalledLauncherDir | Out-Null
    Copy-DirectoryContents -Source $Paths.RuntimeBin -Destination $Paths.InstalledLauncherDir
    if (Test-Path $Paths.RuntimeTransactions) {
        Copy-Item -LiteralPath $Paths.RuntimeTransactions -Destination $Paths.InstalledLauncherDir -Recurse -Force
    }
    Register-SapRpaProtocol -ExePath $Paths.InstalledLauncherExe
    Invoke-Launcher -Paths $Paths -Arguments @("test")
    Set-StepStatus "安装执行器" "Success"

    Initialize-Database $Paths
    Start-LocalApi $Paths
    Check-OnlineStatus $Paths
}

function Initialize-Database {
    param([hashtable]$Paths)
    Set-StepStatus "初始化 SQLite" "Running"
    Ensure-RuntimeDirs $Paths
    if (Test-Path $Paths.RuntimeDb) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupDb = Join-Path (Join-Path $Paths.RuntimeRoot "backups") "sap-rpa-config.$stamp.db"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupDb) | Out-Null
        Copy-Item -LiteralPath $Paths.RuntimeDb -Destination $backupDb -Force
        Write-SetupLog "初始化前已备份 SQLite：$backupDb"
    }
    Invoke-Launcher -Paths $Paths -Arguments @("--init-db")
    Set-StepStatus "初始化 SQLite" "Success"
}

function Reset-Database {
    param([hashtable]$Paths)
    Ensure-RuntimeDirs $Paths
    if (Test-Path $Paths.RuntimeDb) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupDb = Join-Path (Join-Path $Paths.RuntimeRoot "backups") "sap-rpa-config.before-reset.$stamp.db"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupDb) | Out-Null
        Copy-Item -LiteralPath $Paths.RuntimeDb -Destination $backupDb -Force
        Remove-Item -LiteralPath $Paths.RuntimeDb -Force
        Write-SetupLog "已备份并删除旧 SQLite：$backupDb" "WARN"
    }
    Initialize-Database $Paths
}

function Start-LocalApi {
    param([hashtable]$Paths)
    Set-StepStatus "启动本地 API" "Running"
    Ensure-RuntimeDirs $Paths
    $launcher = Get-LauncherExe $Paths
    $existing = Get-CimInstance Win32_Process -Filter "name = 'SapWebLauncher.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "--serve| serve" }
    if ($existing) {
        Write-SetupLog ("本地 API 已在运行，PID：{0}" -f (($existing | Select-Object -ExpandProperty ProcessId) -join ", "))
        Set-StepStatus "启动本地 API" "Success" "Already running"
        return
    }

    $oldHome = $env:SAP_RPA_HOME
    $env:SAP_RPA_HOME = $Paths.RuntimeRoot
    try {
        Start-Process -FilePath $launcher -ArgumentList "--serve" -WorkingDirectory (Split-Path -Parent $launcher) -WindowStyle Hidden
    }
    finally {
        $env:SAP_RPA_HOME = $oldHome
    }
    Start-Sleep -Seconds 2
    Set-StepStatus "启动本地 API" "Success" $script:ApiBaseUrl
}

function Stop-LocalApi {
    $existing = Get-CimInstance Win32_Process -Filter "name = 'SapWebLauncher.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "--serve| serve" }
    if (-not $existing) {
        Write-SetupLog "未发现正在运行的本地 API。"
        return
    }
    foreach ($proc in $existing) {
        Write-SetupLog "停止本地 API，PID：$($proc.ProcessId)" "WARN"
        Stop-Process -Id $proc.ProcessId -Force
    }
}

function Invoke-HealthUrl {
    param([string]$Url)
    try {
        $result = Invoke-RestMethod -Uri $Url -TimeoutSec 6
        Write-SetupLog "API 正常：$Url"
        return $true
    } catch {
        Write-SetupLog "API 检测失败：$Url；$($_.Exception.Message)" "WARN"
        return $false
    }
}

function Check-OnlineStatus {
    param([hashtable]$Paths)
    Set-StepStatus "检测上线状态" "Running"

    $checks = [ordered]@{
        "运行页面" = $Paths.RuntimeIndex
        "ZFI072A VBS" = (Join-Path $Paths.RuntimeTransactions "ZFI072A.vbs")
        "SQLite" = $Paths.RuntimeDb
        "运行目录执行器" = $Paths.RuntimeLauncherExe
        "用户执行器" = $Paths.InstalledLauncherExe
        "SAP 登录配置" = $Paths.SapLoginConfigFile
    }

    foreach ($name in $checks.Keys) {
        $path = $checks[$name]
        if (Test-Path $path) {
            Write-SetupLog "$name：OK - $path"
        } else {
            Write-SetupLog "$name：缺失 - $path" "WARN"
        }
    }

    $protocol = Get-ItemProperty -Path "HKCU:\Software\Classes\sap-rpa\shell\open\command" -ErrorAction SilentlyContinue
    if ($protocol) {
        Write-SetupLog "sap-rpa:// 协议：OK - $($protocol.'(default)')"
    } else {
        Write-SetupLog "sap-rpa:// 协议未注册。" "WARN"
    }

    try {
        Invoke-Launcher -Paths $Paths -Arguments @("test")
    } catch {
        Write-SetupLog "SapWebLauncher self-test 失败：$($_.Exception.Message)" "WARN"
    }

    Invoke-HealthUrl "$($script:ApiBaseUrl)/api/health" | Out-Null
    Invoke-HealthUrl "$($script:ApiBaseUrl)/api/config" | Out-Null
    Invoke-HealthUrl "$($script:ApiBaseUrl)/api/schema" | Out-Null
    Set-StepStatus "检测上线状态" "Success"
}

function Open-RunPage {
    param([hashtable]$Paths)
    if (-not (Test-Path $Paths.RuntimeIndex)) { throw "运行页面不存在：$($Paths.RuntimeIndex)" }
    Start-Process $Paths.RuntimeIndex
    Write-SetupLog "已打开运行页面：$($Paths.RuntimeIndex)"
}

function Get-JsonValue {
    param(
        [object]$Config,
        [string]$Name,
        [string]$DefaultValue = ""
    )
    if ($null -eq $Config) { return $DefaultValue }
    $prop = $Config.PSObject.Properties[$Name]
    if ($prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
        return [string]$prop.Value
    }
    return $DefaultValue
}

function Protect-TextForCurrentUser {
    param([string]$Text)
    try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop } catch { }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    return [Convert]::ToBase64String($protectedBytes)
}

function Save-SapLoginConfig {
    param(
        [hashtable]$Paths,
        [string]$SystemName,
        [string]$Client,
        [string]$UserName,
        [string]$Password,
        [string]$Language,
        [string]$SysNr
    )

    if ([string]::IsNullOrWhiteSpace($SystemName) -or [string]::IsNullOrWhiteSpace($Client) -or [string]::IsNullOrWhiteSpace($UserName)) {
        throw "SAP system、client、user 不能为空。"
    }

    New-Item -ItemType Directory -Force -Path $Paths.SapLoginConfigDir | Out-Null
    $existing = $null
    if (Test-Path $Paths.SapLoginConfigFile) {
        try { $existing = Get-Content -LiteralPath $Paths.SapLoginConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }

    $passwordProtected = Get-JsonValue -Config $existing -Name "passwordProtected"
    if (-not [string]::IsNullOrWhiteSpace($Password)) {
        $passwordProtected = Protect-TextForCurrentUser $Password
    } elseif ([string]::IsNullOrWhiteSpace($passwordProtected)) {
        throw "SAP 密码不能为空；如果要沿用旧密码，必须先存在 passwordProtected。"
    }

    $config = [ordered]@{
        system = $SystemName.Trim()
        client = $Client.Trim()
        user = $UserName.Trim()
        passwordProtected = $passwordProtected
        language = $(if ([string]::IsNullOrWhiteSpace($Language)) { "ZH" } else { $Language.Trim() })
        sysNr = $SysNr.Trim()
    }
    $config | ConvertTo-Json | Set-Content -LiteralPath $Paths.SapLoginConfigFile -Encoding UTF8
    Write-SetupLog "SAP 登录配置已保存，密码已用当前 Windows 用户 DPAPI 保护：$($Paths.SapLoginConfigFile)"
}

function Configure-SapLoginCli {
    param([hashtable]$Paths)
    $existing = $null
    if (Test-Path $Paths.SapLoginConfigFile) {
        try { $existing = Get-Content -LiteralPath $Paths.SapLoginConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }
    $systemName = Read-Host ("SAP system name [{0}]" -f (Get-JsonValue $existing "system"))
    if ([string]::IsNullOrWhiteSpace($systemName)) { $systemName = Get-JsonValue $existing "system" }
    $client = Read-Host ("SAP client [{0}]" -f (Get-JsonValue $existing "client"))
    if ([string]::IsNullOrWhiteSpace($client)) { $client = Get-JsonValue $existing "client" }
    $userName = Read-Host ("SAP user [{0}]" -f (Get-JsonValue $existing "user"))
    if ([string]::IsNullOrWhiteSpace($userName)) { $userName = Get-JsonValue $existing "user" }
    $secure = Read-Host "SAP password, leave empty to keep existing protected password" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $language = Read-Host ("SAP language [{0}]" -f (Get-JsonValue $existing "language" "ZH"))
    if ([string]::IsNullOrWhiteSpace($language)) { $language = Get-JsonValue $existing "language" "ZH" }
    $sysNr = Read-Host ("SAP sysnr [{0}]" -f (Get-JsonValue $existing "sysNr"))
    if ([string]::IsNullOrWhiteSpace($sysNr)) { $sysNr = Get-JsonValue $existing "sysNr" }
    Save-SapLoginConfig -Paths $Paths -SystemName $systemName -Client $client -UserName $userName -Password $password -Language $language -SysNr $sysNr
}

function Show-SapLoginDialog {
    param([hashtable]$Paths)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $existing = $null
    if (Test-Path $Paths.SapLoginConfigFile) {
        try { $existing = Get-Content -LiteralPath $Paths.SapLoginConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "配置 SAP 登录"
    $form.StartPosition = "CenterParent"
    $form.Size = New-Object System.Drawing.Size(460, 360)
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false

    $labels = @("SAP system", "SAP client", "SAP user", "SAP password", "SAP language", "SAP sysnr")
    $defaults = @(
        (Get-JsonValue $existing "system"),
        (Get-JsonValue $existing "client"),
        (Get-JsonValue $existing "user"),
        "",
        (Get-JsonValue $existing "language" "ZH"),
        (Get-JsonValue $existing "sysNr")
    )
    $boxes = @()
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $label = New-Object System.Windows.Forms.Label
        $label.Text = $labels[$i]
        $label.Location = New-Object System.Drawing.Point(22, (24 + $i * 40))
        $label.Size = New-Object System.Drawing.Size(120, 24)
        $form.Controls.Add($label)

        $box = New-Object System.Windows.Forms.TextBox
        $box.Location = New-Object System.Drawing.Point(150, (22 + $i * 40))
        $box.Size = New-Object System.Drawing.Size(260, 24)
        $box.Text = $defaults[$i]
        if ($labels[$i] -eq "SAP password") { $box.UseSystemPasswordChar = $true }
        $form.Controls.Add($box)
        $boxes += $box
    }

    $tip = New-Object System.Windows.Forms.Label
    $tip.Text = "密码只写入当前 Windows 用户 DPAPI 配置；留空表示沿用已有密文。"
    $tip.Location = New-Object System.Drawing.Point(22, 266)
    $tip.Size = New-Object System.Drawing.Size(400, 24)
    $form.Controls.Add($tip)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = "保存"
    $ok.Location = New-Object System.Drawing.Point(235, 292)
    $ok.Size = New-Object System.Drawing.Size(80, 30)
    $ok.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $ok
    $form.Controls.Add($ok)

    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = "取消"
    $cancel.Location = New-Object System.Drawing.Point(330, 292)
    $cancel.Size = New-Object System.Drawing.Size(80, 30)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancel
    $form.Controls.Add($cancel)

    if ($form.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        Save-SapLoginConfig -Paths $Paths -SystemName $boxes[0].Text -Client $boxes[1].Text -UserName $boxes[2].Text -Password $boxes[3].Text -Language $boxes[4].Text -SysNr $boxes[5].Text
    }
}

function Invoke-Action {
    param(
        [string]$ActionName,
        [string]$Runtime,
        [string]$Source,
        [switch]$GuiConfirm
    )

    $paths = Get-InstallPaths -Runtime $Runtime -Source $Source
    Initialize-Log $paths.RuntimeRoot
    Write-SetupLog "运行根目录：$($paths.RuntimeRoot)"
    Write-SetupLog "源码/发布包根目录：$($paths.SourceRoot)"

    if ($ActionName -eq "reset-db" -and ($script:RunningCliMode -eq $true) -and -not $ForceResetDb) {
        throw "CLI 重建 SQLite 必须显式追加 -ForceResetDb。GUI 模式会弹出二次确认。"
    }

    switch ($ActionName) {
        "install" { Install-OrUpgrade $paths }
        "check" { Check-OnlineStatus $paths }
        "start-api" { Start-LocalApi $paths }
        "stop-api" { Stop-LocalApi }
        "open-page" { Open-RunPage $paths }
        "init-db" { Initialize-Database $paths }
        "backup" { New-RuntimeBackup $paths | Out-Null }
        "reset-db" { Reset-Database $paths }
        "configure-login" {
            if ($Cli) { Configure-SapLoginCli $paths } else { Show-SapLoginDialog $paths }
        }
        default { throw "未知动作：$ActionName" }
    }
}

function New-Button {
    param(
        [string]$Text,
        [int]$X,
        [int]$Y,
        [int]$W = 130,
        [int]$H = 32
    )
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size($W, $H)
    return $button
}

function Show-SetupGui {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "SAP RPA V2 Windows Server 一键部署"
    $form.StartPosition = "CenterScreen"
    $form.Size = New-Object System.Drawing.Size(980, 700)
    $form.MinimumSize = New-Object System.Drawing.Size(920, 620)

    $title = New-Object System.Windows.Forms.Label
    $title.Text = "SAP RPA V2 Windows Server 一键部署"
    $title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 15, [System.Drawing.FontStyle]::Bold)
    $title.Location = New-Object System.Drawing.Point(18, 14)
    $title.Size = New-Object System.Drawing.Size(620, 34)
    $form.Controls.Add($title)

    $sub = New-Object System.Windows.Forms.Label
    $sub.Text = "当前用户：$env:USERNAME    API：$script:ApiBaseUrl    SAP GUI 自动化需在交互式桌面运行"
    $sub.Location = New-Object System.Drawing.Point(20, 50)
    $sub.Size = New-Object System.Drawing.Size(880, 24)
    $form.Controls.Add($sub)

    $runtimeLabel = New-Object System.Windows.Forms.Label
    $runtimeLabel.Text = "运行根目录"
    $runtimeLabel.Location = New-Object System.Drawing.Point(20, 88)
    $runtimeLabel.Size = New-Object System.Drawing.Size(110, 24)
    $form.Controls.Add($runtimeLabel)

    $runtimeBox = New-Object System.Windows.Forms.TextBox
    $runtimeBox.Location = New-Object System.Drawing.Point(130, 86)
    $runtimeBox.Size = New-Object System.Drawing.Size(650, 24)
    $runtimeBox.Text = Get-DefaultRuntimeRoot
    $form.Controls.Add($runtimeBox)

    $runtimeChoose = New-Button "选择" 795 82 70 30
    $form.Controls.Add($runtimeChoose)

    $sourceLabel = New-Object System.Windows.Forms.Label
    $sourceLabel.Text = "源码/发布包根目录"
    $sourceLabel.Location = New-Object System.Drawing.Point(20, 124)
    $sourceLabel.Size = New-Object System.Drawing.Size(110, 24)
    $form.Controls.Add($sourceLabel)

    $sourceBox = New-Object System.Windows.Forms.TextBox
    $sourceBox.Location = New-Object System.Drawing.Point(130, 122)
    $sourceBox.Size = New-Object System.Drawing.Size(650, 24)
    $sourceBox.Text = Get-DefaultSourceRoot
    $form.Controls.Add($sourceBox)

    $sourceChoose = New-Button "选择" 795 118 70 30
    $form.Controls.Add($sourceChoose)

    $script:StepList = New-Object System.Windows.Forms.ListView
    $script:StepList.Location = New-Object System.Drawing.Point(20, 168)
    $script:StepList.Size = New-Object System.Drawing.Size(430, 200)
    $script:StepList.View = [System.Windows.Forms.View]::Details
    $script:StepList.FullRowSelect = $true
    $script:StepList.GridLines = $true
    $script:StepList.Columns.Add("步骤", 135) | Out-Null
    $script:StepList.Columns.Add("状态", 90) | Out-Null
    $script:StepList.Columns.Add("说明", 190) | Out-Null
    foreach ($step in @("准备运行目录", "备份当前版本", "复制页面和 VBS", "安装执行器", "初始化 SQLite", "启动本地 API", "检测上线状态")) {
        $item = New-Object System.Windows.Forms.ListViewItem($step)
        $item.SubItems.Add("Pending") | Out-Null
        $item.SubItems.Add("") | Out-Null
        $script:StepList.Items.Add($item) | Out-Null
    }
    $form.Controls.Add($script:StepList)

    $panel = New-Object System.Windows.Forms.GroupBox
    $panel.Text = "操作"
    $panel.Location = New-Object System.Drawing.Point(470, 160)
    $panel.Size = New-Object System.Drawing.Size(470, 210)
    $form.Controls.Add($panel)

    $btnInstall = New-Button "一键部署/升级" 20 30 140 34
    $btnLogin = New-Button "配置 SAP 登录" 170 30 130 34
    $btnCheck = New-Button "检测上线状态" 310 30 130 34
    $btnInitDb = New-Button "初始化/迁移 SQLite" 20 74 140 34
    $btnStartApi = New-Button "启动本地 API" 170 74 130 34
    $btnOpen = New-Button "打开运行页面" 310 74 130 34
    $btnBackup = New-Button "备份当前运行目录" 20 118 140 34
    $btnResetDb = New-Button "备份并重建 SQLite" 170 118 130 34
    $btnStopApi = New-Button "停止本地 API" 310 118 130 34
    $btnLogs = New-Button "打开日志目录" 20 162 130 30
    $btnCopyLog = New-Button "一键复制日志" 165 162 130 30
    $btnSaveLog = New-Button "保存诊断日志" 310 162 130 30
    foreach ($b in @($btnInstall,$btnLogin,$btnCheck,$btnInitDb,$btnStartApi,$btnOpen,$btnBackup,$btnResetDb,$btnStopApi,$btnLogs,$btnCopyLog,$btnSaveLog)) {
        $panel.Controls.Add($b)
    }

    $script:LogBox = New-Object System.Windows.Forms.TextBox
    $script:LogBox.Location = New-Object System.Drawing.Point(20, 388)
    $script:LogBox.Size = New-Object System.Drawing.Size(920, 250)
    $script:LogBox.Multiline = $true
    $script:LogBox.ScrollBars = "Vertical"
    $script:LogBox.ReadOnly = $true
    $script:LogBox.Font = New-Object System.Drawing.Font("Consolas", 9)
    $form.Controls.Add($script:LogBox)

    $chooseFolder = {
        param([System.Windows.Forms.TextBox]$TargetBox)
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = "请选择目录"
        if (Test-Path $TargetBox.Text) { $dialog.SelectedPath = $TargetBox.Text }
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $TargetBox.Text = $dialog.SelectedPath
        }
    }

    $run = {
        param([string]$Name)
        try {
            Invoke-Action -ActionName $Name -Runtime $runtimeBox.Text -Source $sourceBox.Text
            [System.Windows.Forms.MessageBox]::Show("操作完成：$Name", "SAP RPA V2", "OK", "Information") | Out-Null
        } catch {
            Write-SetupLog $_.Exception.Message "ERROR"
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "操作失败", "OK", "Error") | Out-Null
        }
    }

    $runtimeChoose.Add_Click({ & $chooseFolder $runtimeBox })
    $sourceChoose.Add_Click({ & $chooseFolder $sourceBox })
    $btnInstall.Add_Click({ & $run "install" })
    $btnLogin.Add_Click({ & $run "configure-login" })
    $btnCheck.Add_Click({ & $run "check" })
    $btnInitDb.Add_Click({ & $run "init-db" })
    $btnStartApi.Add_Click({ & $run "start-api" })
    $btnOpen.Add_Click({ & $run "open-page" })
    $btnBackup.Add_Click({ & $run "backup" })
    $btnStopApi.Add_Click({
        if ([System.Windows.Forms.MessageBox]::Show("确认停止当前用户下的 SapWebLauncher API 进程？", "确认", "YesNo", "Warning") -eq [System.Windows.Forms.DialogResult]::Yes) {
            & $run "stop-api"
        }
    })
    $btnResetDb.Add_Click({
        $msg = "此操作会先备份现有 SQLite，然后删除并重建空库。运行历史和配置会从当前数据库移除，但备份会保留。确认继续？"
        if ([System.Windows.Forms.MessageBox]::Show($msg, "危险操作确认", "YesNo", "Warning") -eq [System.Windows.Forms.DialogResult]::Yes) {
            & $run "reset-db"
        }
    })
    $btnLogs.Add_Click({
        try {
            $paths = Get-InstallPaths -Runtime $runtimeBox.Text -Source $sourceBox.Text
            New-Item -ItemType Directory -Force -Path $paths.RuntimeLogs | Out-Null
            Start-Process $paths.RuntimeLogs
        } catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "打开失败", "OK", "Error") | Out-Null
        }
    })
    $btnCopyLog.Add_Click({
        try {
            $paths = Get-InstallPaths -Runtime $runtimeBox.Text -Source $sourceBox.Text
            $header = @(
                "SAP RPA V2 installer diagnostic",
                "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
                "User: $env:USERNAME",
                "RuntimeRoot: $($paths.RuntimeRoot)",
                "SourceRoot: $($paths.SourceRoot)",
                "ApiBaseUrl: $script:ApiBaseUrl",
                "LogFile: $script:LogFile",
                ""
            ) -join [Environment]::NewLine
            [System.Windows.Forms.Clipboard]::SetText($header + $script:LogBox.Text)
            [System.Windows.Forms.MessageBox]::Show("诊断日志已复制到剪贴板。", "SAP RPA V2", "OK", "Information") | Out-Null
        } catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "复制失败", "OK", "Error") | Out-Null
        }
    })
    $btnSaveLog.Add_Click({
        try {
            $paths = Get-InstallPaths -Runtime $runtimeBox.Text -Source $sourceBox.Text
            New-Item -ItemType Directory -Force -Path $paths.RuntimeLogs | Out-Null
            $file = Join-Path $paths.RuntimeLogs ("installer-diagnostic-{0}.txt" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
            $content = @(
                "SAP RPA V2 installer diagnostic",
                "Time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
                "User: $env:USERNAME",
                "RuntimeRoot: $($paths.RuntimeRoot)",
                "SourceRoot: $($paths.SourceRoot)",
                "ApiBaseUrl: $script:ApiBaseUrl",
                "LogFile: $script:LogFile",
                "",
                $script:LogBox.Text
            ) -join [Environment]::NewLine
            Set-Content -LiteralPath $file -Encoding UTF8 -Value $content
            [System.Windows.Forms.MessageBox]::Show("诊断日志已保存：`n$file", "SAP RPA V2", "OK", "Information") | Out-Null
        } catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "保存失败", "OK", "Error") | Out-Null
        }
    })

    [void]$form.ShowDialog()
}

if ($Cli -or $Action -ne "gui") {
    $script:RunningCliMode = $true
    $runtime = Get-DefaultRuntimeRoot
    $source = Get-DefaultSourceRoot
    Invoke-Action -ActionName $Action -Runtime $runtime -Source $source
} else {
    Show-SetupGui
}
