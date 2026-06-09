@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$script = Get-Content -LiteralPath '%~dp0scripts\configure_sap_login.ps1' -Raw -Encoding UTF8; & ([ScriptBlock]::Create($script))"
pause
