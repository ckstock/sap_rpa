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

function Get-PeMachineType {
    param([string]$FilePath)

    $stream = $null
    try {
        $stream = [System.IO.File]::OpenRead($FilePath)
        $reader = New-Object System.IO.BinaryReader($stream)

        $stream.Seek(0x3c, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peOffset = $reader.ReadInt32()

        $stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $signature = $reader.ReadUInt32()
        if ($signature -ne 0x00004550) {
            throw "Invalid PE signature."
        }

        $machine = $reader.ReadUInt16()
        switch ($machine) {
            0x014c { return "x86" }
            0x8664 { return "x64" }
            0xaa64 { return "arm64" }
            default { return ("unknown:0x{0:x4}" -f $machine) }
        }
    } finally {
        if ($reader) { $reader.Close() }
        if ($stream) { $stream.Dispose() }
    }
}

function Resolve-Regsvr32 {
    param(
        [string]$FilePath,
        [string]$SapGuiDir
    )

    $machineType = Get-PeMachineType -FilePath $FilePath

    if ($machineType -eq "x86") {
        $regsvr32 = Join-Path $env:WINDIR "SysWOW64\regsvr32.exe"
    } elseif ($machineType -eq "x64") {
        if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
            $regsvr32 = Join-Path $env:WINDIR "Sysnative\regsvr32.exe"
        } else {
            $regsvr32 = Join-Path $env:WINDIR "System32\regsvr32.exe"
        }
    } else {
        if ($SapGuiDir -like "*Program Files (x86)*") {
            $regsvr32 = Join-Path $env:WINDIR "SysWOW64\regsvr32.exe"
        } else {
            $regsvr32 = Join-Path $env:WINDIR "System32\regsvr32.exe"
        }
    }

    if (-not (Test-Path -LiteralPath $regsvr32)) {
        throw "regsvr32.exe was not found."
    }

    return [pscustomobject]@{
        MachineType = $machineType
        Regsvr32 = $regsvr32
    }
}

function Invoke-Regsvr32 {
    param(
        [string]$FilePath,
        [string]$SapGuiDir
    )

    $registration = Resolve-Regsvr32 -FilePath $FilePath -SapGuiDir $SapGuiDir
    Write-Host ("  component: {0}" -f $FilePath)
    Write-Host ("  machine:   {0}" -f $registration.MachineType)
    Write-Host ("  regsvr32:  {0}" -f $registration.Regsvr32)

    $process = Start-Process -FilePath $registration.Regsvr32 -ArgumentList @("/s", "`"$FilePath`"") -Wait -PassThru
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
Write-Host ""
Write-Host "Registration bitness plan:"
@($sapRot, $sapFewse) | ForEach-Object {
    $registration = Resolve-Regsvr32 -FilePath $_ -SapGuiDir $resolvedSapGuiDir
    Write-Host ("  {0}" -f $_)
    Write-Host ("    machine:  {0}" -f $registration.MachineType)
    Write-Host ("    regsvr32: {0}" -f $registration.Regsvr32)
}

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
    Invoke-Regsvr32 -FilePath $sapRot -SapGuiDir $resolvedSapGuiDir
    Invoke-Regsvr32 -FilePath $sapFewse -SapGuiDir $resolvedSapGuiDir
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
