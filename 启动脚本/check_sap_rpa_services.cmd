@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp002_check_sap_rpa_services.ps1"
echo.
pause
