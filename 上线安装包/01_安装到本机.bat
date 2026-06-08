@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$script = Get-Content -LiteralPath '%~dp0scripts\install.ps1' -Raw -Encoding UTF8; & ([ScriptBlock]::Create($script)) -PackageRoot '%~dp0'"
echo.
pause
