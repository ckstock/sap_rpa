@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
if exist "%~dp0SAP定时任务集中管理.exe" (
  start "" "%~dp0SAP定时任务集中管理.exe" %*
  exit /b 0
)
if exist "D:\software\Python314\python.exe" (
  "D:\software\Python314\python.exe" "%~dp0schedule_admin.py" %*
  goto finished
)
where python >nul 2>nul
if not errorlevel 1 (
  python --version >nul 2>nul
  if not errorlevel 1 (
    python "%~dp0schedule_admin.py" %*
    goto finished
  )
)
where py >nul 2>nul
if not errorlevel 1 (
  py -3 --version >nul 2>nul
  if not errorlevel 1 (
    py -3 "%~dp0schedule_admin.py" %*
    goto finished
  )
)
echo SAP定时任务集中管理.exe and Python 3 were not found.
echo Reinstall this management tool or add Python 3 to PATH.
exit /b 1
:finished
if errorlevel 1 pause
endlocal
