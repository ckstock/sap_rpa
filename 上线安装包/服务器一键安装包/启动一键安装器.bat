@echo off
setlocal
chcp 65001 >nul

set "SAP_RPA_SETUP_DIR=%~dp0"
set "SAP_RPA_SETUP_SCRIPT=%SAP_RPA_SETUP_DIR%SapRpaServerSetup.ps1"

if not exist "%SAP_RPA_SETUP_SCRIPT%" (
  echo [FAIL] Setup script not found:
  echo %SAP_RPA_SETUP_SCRIPT%
  pause
  exit /b 1
)

powershell.exe -NoProfile -Command "$env:SAP_RPA_SETUP_DIR=$env:SAP_RPA_SETUP_DIR; $env:SAP_RPA_SOURCE_ROOT=$env:SAP_RPA_SETUP_DIR; $script = Get-Content -LiteralPath $env:SAP_RPA_SETUP_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($script)) -SourceRoot $env:SAP_RPA_SETUP_DIR"
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
  echo.
  echo [FAIL] SAP RPA V2 setup exited with code %EXIT_CODE%.
  pause
)

exit /b %EXIT_CODE%
