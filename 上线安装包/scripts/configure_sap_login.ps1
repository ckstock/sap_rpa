$ErrorActionPreference = "Stop"

$configDir = Join-Path $env:LOCALAPPDATA "SapWebLauncher"
$configFile = Join-Path $configDir "config.json"
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

$config = [ordered]@{
    system = $system
    client = $client
    user = $user
    passwordProtected = $passwordProtected
    language = $language
    sysNr = $sysNr
}

$config | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding UTF8
Write-Host ""
Write-Host "SAP login config saved:" -ForegroundColor Green
Write-Host $configFile
Write-Host ""
Write-Host "Password is protected by Windows DPAPI for the current Windows user. Do not copy config.json to another user or computer." -ForegroundColor Cyan
Write-Host "Netlify page will not pass SAP password. SapWebLauncher reads this local config when sap-rpa:// is opened." -ForegroundColor Cyan
