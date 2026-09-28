param(
    [string]$SwitchProfile = "",
    [switch]$SwitchOnly
)

$ErrorActionPreference = "Stop"

$configDir = Join-Path $env:LOCALAPPDATA "SapWebLauncher"
$configFile = Join-Path $configDir "config.json"
$runtimeRoot = if ([string]::IsNullOrWhiteSpace($env:SAP_RPA_RUNTIME_ROOT)) { "D:\RPA" } else { $env:SAP_RPA_RUNTIME_ROOT }
$runtimeConfigFile = Join-Path $runtimeRoot "config.local.json"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null

try { Add-Type -AssemblyName System.Security -ErrorAction Stop } catch { }
try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop } catch { }

function Get-ExistingValue {
    param(
        [object]$Config,
        [string]$Name,
        [string]$DefaultValue = ""
    )
    if ($null -eq $Config) {
        return $DefaultValue
    }
    if ($Config -is [hashtable] -and $Config.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$Config[$Name])) {
        return [string]$Config[$Name]
    }

    $prop = $Config.PSObject.Properties[$Name]
    if ($null -ne $prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
        return [string]$prop.Value
    }
    return $DefaultValue
}

function Read-Required {
    param(
        [string]$Prompt,
        [string]$DefaultValue = ""
    )
    while ($true) {
        $label = if ($DefaultValue) { "$Prompt [$DefaultValue]" } else { $Prompt }
        $value = Read-Host $label
        if ([string]::IsNullOrWhiteSpace($value) -and $DefaultValue) { return $DefaultValue }
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
        Write-Host "This value is required." -ForegroundColor Yellow
    }
}

function Read-Optional {
    param(
        [string]$Prompt,
        [string]$DefaultValue = ""
    )
    $label = if ($DefaultValue) { "$Prompt [$DefaultValue]" } else { $Prompt }
    $value = Read-Host $label
    if ([string]::IsNullOrWhiteSpace($value)) { return $DefaultValue }
    return $value.Trim()
}

function Read-SecretText {
    param([string]$Prompt)
    $secure = Read-Host $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Protect-TextForCurrentUser {
    param([string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    )
    return [Convert]::ToBase64String($protectedBytes)
}

function Set-JsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Value
    )
    if ($Object.PSObject.Properties[$Name]) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Get-ProfileValue {
    param(
        [object]$Profile,
        [string]$Name,
        [string]$DefaultValue = ""
    )
    return Get-ExistingValue $Profile $Name $DefaultValue
}

function Get-SavedProfiles {
    param([object]$Config)
    if ($null -eq $Config -or $null -eq $Config.PSObject.Properties["profiles"]) {
        return @()
    }
    return @($Config.profiles | Where-Object { -not [string]::IsNullOrWhiteSpace((Get-ProfileValue $_ "key" "")) })
}

function Find-SavedProfile {
    param(
        [object[]]$Profiles,
        [string]$Key
    )
    return @($Profiles | Where-Object {
        (Get-ProfileValue $_ "key" "").Equals($Key.Trim(), [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1)
}

function New-LegacyProfile {
    param(
        [object]$Config,
        [object]$RuntimeConfig
    )
    $key = Get-ExistingValue $Config "activeProfile" ""
    if ([string]::IsNullOrWhiteSpace($key)) {
        $key = if ((Get-ExistingValue $Config "system" "") -eq "test888") { "T" } else { Get-ExistingValue $Config "system" "CURRENT" }
    }
    $sapNco = if ($RuntimeConfig.PSObject.Properties["sapNco"]) { $RuntimeConfig.sapNco } else { [pscustomobject]@{} }
    return [pscustomobject]@{
        key = $key
        system = Get-ExistingValue $Config "system" ""
        client = Get-ExistingValue $Config "client" ""
        user = Get-ExistingValue $Config "user" ""
        passwordProtected = Get-ExistingValue $Config "passwordProtected" ""
        language = Get-ExistingValue $Config "language" "ZH"
        sysNr = Get-ExistingValue $Config "sysNr" ""
        sapNco = $sapNco
    }
}

function Write-ActiveProfile {
    param(
        [object]$Profile,
        [object]$Config,
        [object]$RuntimeConfig
    )
    foreach ($name in @("system", "client", "user", "passwordProtected", "language", "sysNr")) {
        Set-JsonProperty $Config $name (Get-ProfileValue $Profile $name "")
    }
    Set-JsonProperty $Config "activeProfile" (Get-ProfileValue $Profile "key" "")

    $sapNco = if ($Profile.PSObject.Properties["sapNco"]) { $Profile.sapNco } else { [pscustomobject]@{} }
    Set-JsonProperty $RuntimeConfig "sapNco" $sapNco
    [System.IO.File]::WriteAllText($configFile, ($Config | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($runtimeConfigFile, ($RuntimeConfig | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
}

function Read-RuntimeLocalConfig {
    if (Test-Path $runtimeConfigFile) {
        try {
            return Get-Content $runtimeConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Write-Host "Existing runtime config is invalid, it will be overwritten: $runtimeConfigFile" -ForegroundColor Yellow
        }
    }

    return [pscustomobject]@{
        multiLogonPolicy = "takeover"
    }
}

$existing = $null
if (Test-Path $configFile) {
    try {
        $existing = Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-Host "Existing config is invalid, it will be overwritten: $configFile" -ForegroundColor Yellow
    }
}

$runtimeExisting = Read-RuntimeLocalConfig
$profiles = Get-SavedProfiles $existing
if (@($profiles).Count -eq 0 -and $null -ne $existing -and ($existing.PSObject.Properties.Name -contains "profiles")) {
    # Windows PowerShell versions differ in how ConvertFrom-Json arrays flow through a function pipeline.
    $profilesProperty = @($existing.PSObject.Properties | Where-Object { $_.Name -eq "profiles" } | Select-Object -First 1)
    if ($profilesProperty.Count -gt 0) {
        $profiles = @($profilesProperty[0].Value)
    }
}
if (@($profiles).Count -eq 0 -and $null -ne $existing -and -not [string]::IsNullOrWhiteSpace((Get-ExistingValue $existing "system" ""))) {
    $profiles = @(New-LegacyProfile $existing $runtimeExisting)
    Write-Host "Migrated the current SAP configuration into a saved profile: $($profiles[0].key)" -ForegroundColor Yellow
    Set-JsonProperty $existing "profiles" $profiles
    Set-JsonProperty $existing "activeProfile" (Get-ProfileValue $profiles[0] "key" "")
    [System.IO.File]::WriteAllText($configFile, ($existing | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
}

if (@($profiles).Count -gt 0) {
    $activeProfileKey = Get-ExistingValue $existing "activeProfile" (Get-ExistingValue $existing "system" "")
    Write-Host ""
    Write-Host "已保存的 SAP 系统配置（请根据方括号内的代号选择）：" -ForegroundColor Cyan
    foreach ($profile in $profiles) {
        $profileKey = Get-ProfileValue $profile "key" ""
        $profileSystem = Get-ProfileValue $profile "system" ""
        $marker = if ($profileKey -eq $activeProfileKey) { "（当前使用）" } else { "" }
        Write-Host "  [$profileKey] SAP系统：$profileSystem$marker"
    }
    $action = if ($SwitchOnly -or -not [string]::IsNullOrWhiteSpace($SwitchProfile)) { "S" } else { (Read-Optional "请选择操作：S=切换已有配置，C=新增/修改配置，N=保持当前配置" "N").ToUpperInvariant() }
    if ($action -eq "S") {
        $selectedKey = if (-not [string]::IsNullOrWhiteSpace($SwitchProfile)) { $SwitchProfile } else { Read-Required "请从上面列表中选择一个系统：只输入方括号里的代号（例如 T、P、U），然后回车" "" }
        $selectedProfile = @(Find-SavedProfile $profiles $selectedKey)
        if ($selectedProfile.Count -eq 0) { throw "Saved SAP profile not found: $selectedKey" }
        Write-ActiveProfile $selectedProfile[0] $existing $runtimeExisting
        Write-Host "已切换到系统配置 '$selectedKey'。" -ForegroundColor Green
        Write-Host "开始新任务前请重启 SapWebLauncher，使 SAP GUI 和 NCo 使用同一套系统配置。" -ForegroundColor Yellow
        exit 0
    }
    if ($action -ne "C") {
        Write-Host "Current SAP system was kept. No configuration was changed." -ForegroundColor Green
        exit 0
    }
}
if ($SwitchOnly -or -not [string]::IsNullOrWhiteSpace($SwitchProfile)) {
    throw "没有可切换的系统配置，请先运行 04_配置SAP登录信息.bat 新增配置。"
}

$profileKey = Read-Required "请输入系统配置简称/切换代号（例如 T=测试、P=正式、U=预生产；以后用此代号切换系统）" ""
$profileExistingArray = @(Find-SavedProfile $profiles $profileKey)
$profileExisting = if ($profileExistingArray.Count -gt 0) { $profileExistingArray[0] } else { $null }
$profileSapNco = if ($null -ne $profileExisting -and $profileExisting.PSObject.Properties["sapNco"]) { $profileExisting.sapNco } elseif ($runtimeExisting.PSObject.Properties["sapNco"]) { $runtimeExisting.sapNco } else { [pscustomobject]@{} }

$system = Read-Required "SAP system name" (Get-ExistingValue $profileExisting "system" (Get-ExistingValue $existing "system" ""))
$client = Read-Required "SAP client" (Get-ExistingValue $profileExisting "client" (Get-ExistingValue $existing "client" ""))
$user = Read-Required "SAP user" (Get-ExistingValue $profileExisting "user" (Get-ExistingValue $existing "user" ""))
$password = Read-SecretText "SAP password (blank keeps the saved password)"
$passwordProtected = Get-ExistingValue $profileExisting "passwordProtected" (Get-ExistingValue $existing "passwordProtected" "")
if ([string]::IsNullOrWhiteSpace($password)) {
    $legacyPassword = Get-ExistingValue $profileExisting "password" (Get-ExistingValue $existing "password" "")
    if (-not [string]::IsNullOrWhiteSpace($legacyPassword) -and [string]::IsNullOrWhiteSpace($passwordProtected)) {
        $passwordProtected = Protect-TextForCurrentUser $legacyPassword
        Write-Host "Existing plaintext password was migrated to Windows DPAPI protection." -ForegroundColor Yellow
    }
    elseif ([string]::IsNullOrWhiteSpace($passwordProtected)) { throw "SAP password is required for a new profile." }
}
else { $passwordProtected = Protect-TextForCurrentUser $password }
$language = Read-Required "SAP language" (Get-ExistingValue $profileExisting "language" (Get-ExistingValue $existing "language" "ZH"))
$sysNr = Read-Optional "SAP sysnr (GUI direct connection only; leave empty for logon group)" (Get-ExistingValue $profileExisting "sysNr" (Get-ExistingValue $existing "sysNr" ""))

Write-Host ""
Write-Host "ZFI019NL memory fetch uses SAP NCo/RFC." -ForegroundColor Cyan
Write-Host "Choose direct application server or SAP logon group. The two modes use different NCo parameters." -ForegroundColor Cyan
$existingSapNco = $null
if ($null -ne $profileSapNco) { $existingSapNco = $profileSapNco }
$existingFileStorage = $null
if ($runtimeExisting.PSObject.Properties["fileStorage"]) {
    $existingFileStorage = $runtimeExisting.PSObject.Properties["fileStorage"].Value
}
$defaultAlvExportDataDirectory = Join-Path $runtimeRoot "临时文件\文件数据"
$alvExportDataDirectory = Read-Optional "ALV/Excel export data directory" (Get-ExistingValue $existingFileStorage "alvExportDataDirectory" $defaultAlvExportDataDirectory)
$ncoConnectionNameDefault = if ($null -ne $profileExisting) {
    Get-ExistingValue $existingSapNco "connectionName" $system
}
else {
    $system
}
$ncoConnectionName = Read-Required "SAP NCo connection name" $ncoConnectionNameDefault
$connectionModeInput = Read-Optional "SAP connection mode (D=direct, G=SAP logon group)" (Get-ExistingValue $existingSapNco "connectionMode" "D")
$connectionMode = if ($connectionModeInput -match "^(G|GROUP|MESSAGESERVER|LOGONGROUP)$") { "messageServer" } else { "direct" }
$ncoIpAddress = ""
$ncoSystemNumber = ""
$ncoMessageServerHost = ""
$ncoMessageServerService = ""
$ncoLogonGroup = ""
if ($connectionMode -eq "messageServer") {
    $ncoMessageServerHost = Read-Required "SAP NCo message server host / MSHOST" (Get-ExistingValue $existingSapNco "messageServerHost" "")
    $ncoMessageServerService = Read-Optional "SAP NCo message server service / port (for example 3611)" (Get-ExistingValue $existingSapNco "messageServerService" (Get-ExistingValue $existingSapNco "messageServerPort" ""))
    $ncoLogonGroup = Read-Required "SAP NCo logon group" (Get-ExistingValue $existingSapNco "logonGroup" (Get-ExistingValue $existingSapNco "groupName" "PUBLIC"))
}
else {
    $ncoIpAddress = Read-Required "SAP NCo app server / ipAddress" (Get-ExistingValue $existingSapNco "ipAddress" "")
    $ncoSystemNumber = Read-Required "SAP NCo instance number / systemNumber" (Get-ExistingValue $existingSapNco "systemNumber" $sysNr)
}
$ncoSystemId = Read-Required "SAP NCo system id / SID" (Get-ExistingValue $existingSapNco "systemId" $system)
$ncoRouter = Read-Optional "SAP Router, leave empty if not used" (Get-ExistingValue $existingSapNco "router" "")

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
$sapNco = [pscustomobject]@{
    connectionMode = $connectionMode
    connectionName = $ncoConnectionName
    ipAddress = $ncoIpAddress
    systemNumber = $ncoSystemNumber
    systemId = $ncoSystemId
    messageServerHost = $ncoMessageServerHost
    messageServerService = $ncoMessageServerService
    logonGroup = $ncoLogonGroup
    router = $ncoRouter
}

$newProfile = [pscustomobject]@{
    key = $profileKey
    system = $system
    client = $client
    user = $user
    passwordProtected = $passwordProtected
    language = $language
    sysNr = $sysNr
    sapNco = $sapNco
}
$updatedProfiles = @($profiles | Where-Object { -not (Get-ProfileValue $_ "key" "").Equals($profileKey, [System.StringComparison]::OrdinalIgnoreCase) })
$updatedProfiles += $newProfile
if ($null -eq $existing) { $existing = [pscustomobject]@{} }
foreach ($name in @("system", "client", "user", "passwordProtected", "language", "sysNr")) {
    Set-JsonProperty $existing $name (Get-ProfileValue $newProfile $name "")
}
Set-JsonProperty $existing "activeProfile" $profileKey
Set-JsonProperty $existing "profiles" $updatedProfiles
[System.IO.File]::WriteAllText($configFile, ($existing | ConvertTo-Json -Depth 12), [System.Text.UTF8Encoding]::new($false))
Set-JsonProperty $runtimeExisting "sapNco" $sapNco
if (-not $runtimeExisting.PSObject.Properties["fileStorage"]) {
    Set-JsonProperty $runtimeExisting "fileStorage" ([pscustomobject]@{})
}
Set-JsonProperty $runtimeExisting.fileStorage "alvExportDataDirectory" $alvExportDataDirectory
if (-not $runtimeExisting.PSObject.Properties["zfi057Workflow"]) {
    Set-JsonProperty $runtimeExisting "zfi057Workflow" ([pscustomobject]@{})
}
if (-not $runtimeExisting.zfi057Workflow.PSObject.Properties["zfi019nlMemory"]) {
    Set-JsonProperty $runtimeExisting.zfi057Workflow "zfi019nlMemory" ([pscustomobject]@{
        report = "ZFI019NL"
        memoryId = "%ZFI019NA%"
        memoryName = "GT_ALV"
        spoolDevice = "LP01"
        waitSeconds = 60
        splitTable = "ZFI_SPLIT"
        splitBukrs = "2030"
        dongtaiBusinessAreas = @("0162", "7700", "7800", "7600", "1070", "0500", "7900")
    })
}
$runtimeJson = $runtimeExisting | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($runtimeConfigFile, $runtimeJson, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "SAP login config saved:" -ForegroundColor Green
Write-Host $configFile
Write-Host "SAP NCo runtime target saved:" -ForegroundColor Green
Write-Host $runtimeConfigFile
Write-Host "ALV/Excel export data directory:" -ForegroundColor Green
Write-Host $alvExportDataDirectory
Write-Host ""
Write-Host "Password is protected by Windows DPAPI for the current Windows user. Do not copy config.json to another user or computer." -ForegroundColor Cyan
Write-Host "Profile '$profileKey' is now active. Use 04_切换SAP系统.bat or 04_切换SAP系统.bat P to switch without re-entering credentials." -ForegroundColor Cyan
Write-Host "NCo direct mode uses app server + system number; messageServer mode uses message server + service + SID + logon group." -ForegroundColor Cyan
Write-Host "ALV/Excel export path is read when SapWebLauncher starts. Restart the service after changing config.local.json." -ForegroundColor Cyan
Write-Host "Netlify page will not pass SAP password. SapWebLauncher reads this local config when sap-rpa:// is opened." -ForegroundColor Cyan
