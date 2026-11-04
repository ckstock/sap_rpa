@echo off
setlocal
cd /d D:\RPA
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\RPA\启动脚本\start_sap_rpa_interactive.ps1"
set "exitCode=%ERRORLEVEL%"
echo.
if not "%exitCode%"=="0" echo Interactive start failed. Review the message above.
pause
exit /b %exitCode%
