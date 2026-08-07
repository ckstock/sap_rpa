param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$GitNexusArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = "D:\RPA\RpaProject"
$globalGitNexus = "C:\Users\Marcus\AppData\Roaming\npm\gitnexus.cmd"
$nodeExe = "D:\Program Files\nodejs\node.exe"
$projectGitNexus = Join-Path $repoRoot ".gitnexus\run.cjs"

if (-not (Test-Path -LiteralPath $repoRoot)) {
    throw "Repository not found: $repoRoot"
}

Set-Location -LiteralPath $repoRoot

if (Test-Path -LiteralPath $globalGitNexus) {
    & $globalGitNexus @GitNexusArgs
    exit $LASTEXITCODE
}

if ((Test-Path -LiteralPath $nodeExe) -and (Test-Path -LiteralPath $projectGitNexus)) {
    & $nodeExe $projectGitNexus @GitNexusArgs
    exit $LASTEXITCODE
}

throw "GitNexus is not available. Tried: $globalGitNexus and $nodeExe $projectGitNexus"
