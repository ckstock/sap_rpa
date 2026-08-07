@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "SCRIPT=%SCRIPT_DIR%00_register_sap_gui_components.ps1"
if not exist "%SCRIPT%" (
  echo Missing script: %SCRIPT%
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -Verb RunAs -FilePath powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','%SCRIPT%')"
echo.
echo A Windows UAC prompt should open. Approve it to register SAP GUI scripting components.
echo After the elevated window closes, you can verify with:
echo powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -CheckOnly
echo.
pause
