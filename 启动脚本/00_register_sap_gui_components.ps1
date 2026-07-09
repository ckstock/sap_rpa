param(
    [switch]$CheckOnly,
    [string]$SapGuiDir = ""
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-SapGuiDir {
    param([string]$Configured)

    $candidates = @()
    if ($Configured) { $candidates += $Configured }
    $candidates += @(
        "C:\Program Files (x86)\SAP\FrontEnd\SAPgui",
        "C:\Program Files\SAP\FrontEnd\SAPgui"
    )

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $full = [Environment]::ExpandEnvironmentVariables($candidate)
        if ((Test-Path -LiteralPath (Join-Path $full "saprotwr.dll")) -and
            (Test-Path -LiteralPath (Join-Path $full "sapfewse.ocx"))) {
            return (Resolve-Path -LiteralPath $full).Path
        }
    }

    throw "SAP GUI directory was not found. Install SAP GUI or pass -SapGuiDir."
}

function Test-ProgId {
    param([string]$ProgId)
    try {
        $type = [type]::GetTypeFromProgID($ProgId)
        return $null -ne $type
    } catch {
        return $false
    }
}

function Invoke-Regsvr32 {
    param([string]$FilePath)

    $regsvr32 = Join-Path $env:WINDIR "SysWOW64\regsvr32.exe"
    if (-not (Test-Path -LiteralPath $regsvr32)) {
        $regsvr32 = Join-Path $env:WINDIR "System32\regsvr32.exe"
    }
    if (-not (Test-Path -LiteralPath $regsvr32)) {
        throw "regsvr32.exe was not found."
    }

    $process = Start-Process -FilePath $regsvr32 -ArgumentList @("/s", "`"$FilePath`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "regsvr32 failed for $FilePath with exit code $($process.ExitCode)."
    }
}

$resolvedSapGuiDir = Resolve-SapGuiDir -Configured $SapGuiDir
$sapRot = Join-Path $resolvedSapGuiDir "saprotwr.dll"
$sapFewse = Join-Path $resolvedSapGuiDir "sapfewse.ocx"

Write-Host "SAP GUI directory: $resolvedSapGuiDir"
Write-Host "saprotwr.dll:      $sapRot"
Write-Host "sapfewse.ocx:      $sapFewse"

$before = [ordered]@{
    "SapROTWr.SapROTWrapper" = Test-ProgId "SapROTWr.SapROTWrapper"
    "Sapgui.ScriptingCtrl.1" = Test-ProgId "Sapgui.ScriptingCtrl.1"
}

Write-Host ""
Write-Host "Before:"
$before.GetEnumerator() | ForEach-Object {
    Write-Host ("  {0}: {1}" -f $_.Key, $_.Value)
}

if (-not $CheckOnly) {
    if (-not (Test-IsAdmin)) {
        throw "Administrator privileges are required. Run 00_register_sap_gui_components.cmd or open an elevated PowerShell."
    }

    Write-Host ""
    Write-Host "Registering SAP GUI scripting components..."
    Invoke-Regsvr32 -FilePath $sapRot
    Invoke-Regsvr32 -FilePath $sapFewse
}

$after = [ordered]@{
    "SapROTWr.SapROTWrapper" = Test-ProgId "SapROTWr.SapROTWrapper"
    "Sapgui.ScriptingCtrl.1" = Test-ProgId "Sapgui.ScriptingCtrl.1"
}

Write-Host ""
Write-Host "After:"
$after.GetEnumerator() | ForEach-Object {
    Write-Host ("  {0}: {1}" -f $_.Key, $_.Value)
}

$failed = @($after.GetEnumerator() | Where-Object { -not $_.Value })
if ($failed.Count -gt 0) {
    throw "SAP GUI scripting COM registration is incomplete: $($failed.Key -join ', ')"
}

Write-Host ""
Write-Host "SAP GUI scripting COM registration is ready."
