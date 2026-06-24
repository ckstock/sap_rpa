@echo off
setlocal
cd /d "%~dp0"
set "SAP_RPA_DEPLOY_SCRIPT_ROOT=%~dp0scripts"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$env:SAP_RPA_DEPLOY_SCRIPT_ROOT='%SAP_RPA_DEPLOY_SCRIPT_ROOT%'; $script = Get-Content -LiteralPath '%~dp0scripts\00_prepare_runtime.ps1' -Raw -Encoding UTF8; & ([ScriptBlock]::Create($script))"
echo.
pause
