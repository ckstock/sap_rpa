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

$system = Read-Required "SAP system name" (Get-ExistingValue $existing "system" "")
$client = Read-Required "SAP client" (Get-ExistingValue $existing "client" "")
$user = Read-Required "SAP user" (Get-ExistingValue $existing "user" "")
$password = Read-SecretText "SAP password"
$passwordProtected = Get-ExistingValue $existing "passwordProtected" ""
if ([string]::IsNullOrWhiteSpace($password)) {
    $legacyPassword = Get-ExistingValue $existing "password" ""
    if (-not [string]::IsNullOrWhiteSpace($legacyPassword)) {
        $passwordProtected = Protect-TextForCurrentUser $legacyPassword
        Write-Host "Existing plaintext password was migrated to Windows DPAPI protection." -ForegroundColor Yellow
    }
    elseif ([string]::IsNullOrWhiteSpace($passwordProtected)) {
        throw "SAP password is required."
    }
}
else {
    $passwordProtected = Protect-TextForCurrentUser $password
}
$language = Read-Required "SAP language" (Get-ExistingValue $existing "language" "ZH")
$sysNr = Read-Required "SAP sysnr" (Get-ExistingValue $existing "sysNr" "")

Write-Host ""
Write-Host "ZFI019NL memory fetch uses SAP NCo/RFC. Please enter the direct connection target." -ForegroundColor Cyan
Write-Host "If this is the current test system, use: name=test888, server=10.0.40.212, instance=10, systemId=TD1." -ForegroundColor Cyan
$runtimeExisting = Read-RuntimeLocalConfig
$existingSapNco = $null
if ($runtimeExisting.PSObject.Properties["sapNco"]) {
    $existingSapNco = $runtimeExisting.PSObject.Properties["sapNco"].Value
}
$ncoConnectionName = Read-Required "SAP NCo connection name" (Get-ExistingValue $existingSapNco "connectionName" $system)
$ncoIpAddress = Read-Required "SAP NCo app server / ipAddress" (Get-ExistingValue $existingSapNco "ipAddress" "")
$ncoSystemNumber = Read-Required "SAP NCo instance number / systemNumber" (Get-ExistingValue $existingSapNco "systemNumber" $sysNr)
$ncoSystemId = Read-Required "SAP NCo system id / SID" (Get-ExistingValue $existingSapNco "systemId" $system)
$ncoRouter = Read-Optional "SAP Router, leave empty if not used" (Get-ExistingValue $existingSapNco "router" "")

$config = [ordered]@{
    system = $system
    client = $client
    user = $user
    passwordProtected = $passwordProtected
    language = $language
    sysNr = $sysNr
}

$configJson = $config | ConvertTo-Json
[System.IO.File]::WriteAllText($configFile, $configJson, [System.Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
$sapNco = [pscustomobject]@{
    connectionName = $ncoConnectionName
    ipAddress = $ncoIpAddress
    systemNumber = $ncoSystemNumber
    systemId = $ncoSystemId
    router = $ncoRouter
}
Set-JsonProperty $runtimeExisting "sapNco" $sapNco
if (-not $runtimeExisting.PSObject.Properties["zfi057Workflow"]) {
    Set-JsonProperty $runtimeExisting "zfi057Workflow" ([pscustomobject]@{})
}
if (-not $runtimeExisting.zfi057Workflow.PSObject.Properties["gs03SetName"]) {
    Set-JsonProperty $runtimeExisting.zfi057Workflow "gs03SetName" "Z31"
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
Write-Host ""
Write-Host "Password is protected by Windows DPAPI for the current Windows user. Do not copy config.json to another user or computer." -ForegroundColor Cyan
Write-Host "NCo uses the same client/user/password/language unless sapNco explicitly overrides them. config.local.json stores server/SID/instance only by default." -ForegroundColor Cyan
Write-Host "Netlify page will not pass SAP password. SapWebLauncher reads this local config when sap-rpa:// is opened." -ForegroundColor Cyan
