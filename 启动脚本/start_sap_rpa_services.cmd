@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp001_start_sap_rpa_services.ps1"
echo.
pause
