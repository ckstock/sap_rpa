@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "SAP_RPA_SETUP_DIR=%~dp0"
for %%I in ("%SAP_RPA_SETUP_DIR%..\..") do set "SAP_RPA_SOURCE_ROOT=%%~fI"

if "%SAP_RPA_RUNTIME_ROOT%"=="" (
  if "%SAP_RPA_HOME%"=="" (
    if exist D:\ (
      set "SAP_RPA_RUNTIME_ROOT=D:\SAP_RPA"
    ) else (
      set "SAP_RPA_RUNTIME_ROOT=C:\SAP_RPA"
    )
  ) else (
    set "SAP_RPA_RUNTIME_ROOT=%SAP_RPA_HOME%"
  )
)

if "%SAP_RPA_GIT_BRANCH%"=="" set "SAP_RPA_GIT_BRANCH=codex/v2-local-api-sqlite"
set "SAP_RPA_SETUP_SCRIPT=%SAP_RPA_SETUP_DIR%SapRpaServerSetup.ps1"

echo [INFO] Source repository: %SAP_RPA_SOURCE_ROOT%
echo [INFO] Runtime root: %SAP_RPA_RUNTIME_ROOT%
echo [INFO] Git branch: %SAP_RPA_GIT_BRANCH%
echo.

if not exist "%SAP_RPA_SOURCE_ROOT%\.git" (
  echo [FAIL] This entry must be run from a Git clone of sap_rpa.
  echo [FAIL] Clone first, for example:
  echo git clone -b %SAP_RPA_GIT_BRANCH% https://github.com/ckstock/sap_rpa.git D:\deploy\sap_rpa
  pause
  exit /b 1
)

if not exist "%SAP_RPA_SETUP_SCRIPT%" (
  echo [FAIL] Setup script not found:
  echo %SAP_RPA_SETUP_SCRIPT%
  pause
  exit /b 1
)

where git.exe >nul 2>nul
if not "%ERRORLEVEL%"=="0" (
  echo [FAIL] git.exe not found. Install Git for Windows or add it to PATH.
  pause
  exit /b 1
)

echo [INFO] Fetching latest code from GitHub...
git -C "%SAP_RPA_SOURCE_ROOT%" fetch origin "%SAP_RPA_GIT_BRANCH%"
if not "%ERRORLEVEL%"=="0" goto :git_failed

git -C "%SAP_RPA_SOURCE_ROOT%" checkout "%SAP_RPA_GIT_BRANCH%"
if not "%ERRORLEVEL%"=="0" goto :git_failed

git -C "%SAP_RPA_SOURCE_ROOT%" pull --ff-only origin "%SAP_RPA_GIT_BRANCH%"
if not "%ERRORLEVEL%"=="0" goto :git_failed

echo.
echo [INFO] Installing/upgrading SAP RPA V2 on port 8080...
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $script = Get-Content -LiteralPath $env:SAP_RPA_SETUP_SCRIPT -Raw -Encoding UTF8; & ([ScriptBlock]::Create($script)) -Cli -Action install -RuntimeRoot $env:SAP_RPA_RUNTIME_ROOT -SourceRoot $env:SAP_RPA_SOURCE_ROOT -ApiPort 8080"
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
  echo.
  echo [FAIL] SAP RPA V2 GitHub update/install exited with code %EXIT_CODE%.
  pause
  exit /b %EXIT_CODE%
)

echo.
echo [OK] SAP RPA V2 updated and installed.
echo [OK] API: http://127.0.0.1:8080
if not "%SAP_RPA_NO_PAUSE%"=="1" pause
exit /b 0

:git_failed
echo.
echo [FAIL] Git update failed. Check network, branch, permissions, or local uncommitted changes.
echo [INFO] Local config must stay in %SAP_RPA_RUNTIME_ROOT%\config.local.json, not in the Git repository.
pause
exit /b 1
