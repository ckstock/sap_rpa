@echo off
setlocal
set "APP=%~dp0SapWebLauncher\bin\Release\net8.0-windows\SapWebLauncher.exe"
if not exist "%APP%" set "APP=%~dp0SapWebLauncher\bin\x64\Release\net8.0-windows\SapWebLauncher.exe"
if not exist "%APP%" (
  echo SapWebLauncher.exe not found. Build the project first.
  pause
  exit /b 1
)
"%APP%" --register
echo.
echo Registered sap-rpa://
pause
