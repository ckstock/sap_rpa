@echo off
setlocal
cd /d D:\RPA
echo Registering SAP RPA interactive autostart for SR-FIN-MANAG-01\marcus...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\RPA\启动脚本\05_register_sap_rpa_startup_monitor.ps1" -RuntimeRoot "D:\RPA" -HttpCompatibilityHost "10.0.2.120" -RunAsUser "SR-FIN-MANAG-01\marcus" -TriggerMode AtLogon
set "exitCode=%ERRORLEVEL%"
echo.
if not "%exitCode%"=="0" echo Interactive autostart registration failed. Review the message above.
if "%exitCode%"=="0" (
  echo Interactive autostart registered. Restarting the local API in the current marcus desktop session...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\RPA\启动脚本\01_start_sap_rpa_services.ps1" -RuntimeRoot "D:\RPA" -HttpCompatibilityHost "10.0.2.120" -RestartLauncher
  set "exitCode=%ERRORLEVEL%"
)
if "%exitCode%"=="0" echo Interactive autostart is active. Verify both tasks show At log on.
pause
exit /b %exitCode%
