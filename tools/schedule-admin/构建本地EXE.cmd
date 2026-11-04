@echo off
chcp 65001 >nul
setlocal

set "PYTHON=D:\RPA\.tools\schedule-admin-builder\Scripts\python.exe"
set "SOURCE=%~dp0schedule_admin.py"
set "OUTPUT_ROOT=D:\RPA\build\schedule-admin"
set "DIST_DIR=%OUTPUT_ROOT%\dist"
set "WORK_DIR=%OUTPUT_ROOT%\work"
set "SPEC_DIR=%OUTPUT_ROOT%\spec"
set "TCL_SOURCE=D:\software\Python314\tcl\libtcl9.0.4.zip"
set "TK_SOURCE=D:\software\Python314\tcl\libtk9.0.4.zip"
set "TCL_DATA=%OUTPUT_ROOT%\tcl-data"
set "TK_DATA=%OUTPUT_ROOT%\tk-data"

if not exist "%PYTHON%" (
  echo PyInstaller build environment was not found:
  echo   %PYTHON%
  echo Install PyInstaller in the dedicated builder environment first.
  pause
  exit /b 1
)

if not exist "%SOURCE%" (
  echo Source file was not found:
  echo   %SOURCE%
  pause
  exit /b 1
)

if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
if exist "%WORK_DIR%" rmdir /s /q "%WORK_DIR%"
if exist "%SPEC_DIR%" rmdir /s /q "%SPEC_DIR%"
if exist "%TCL_DATA%" rmdir /s /q "%TCL_DATA%"
if exist "%TK_DATA%" rmdir /s /q "%TK_DATA%"
mkdir "%DIST_DIR%" >nul 2>nul
mkdir "%WORK_DIR%" >nul 2>nul
mkdir "%SPEC_DIR%" >nul 2>nul
mkdir "%TCL_DATA%" >nul 2>nul
mkdir "%TK_DATA%" >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%TCL_SOURCE%' -DestinationPath '%TCL_DATA%' -Force; Expand-Archive -LiteralPath '%TK_SOURCE%' -DestinationPath '%TK_DATA%' -Force"
if errorlevel 1 (
  echo Tcl/Tk library extraction failed.
  pause
  exit /b 1
)

"%PYTHON%" -m PyInstaller ^
  --noconfirm ^
  --clean ^
  --onefile ^
  --windowed ^
  --name SAP_Schedule_Admin ^
  --add-data "%TCL_DATA%\tcl_library;lib\tcl\tcl_library" ^
  --add-data "%TK_DATA%\tk_library;lib\tk\tk_library" ^
  --distpath "%DIST_DIR%" ^
  --workpath "%WORK_DIR%" ^
  --specpath "%SPEC_DIR%" ^
  "%SOURCE%"

if errorlevel 1 (
  echo EXE build failed.
  pause
  exit /b 1
)

echo EXE build completed:
powershell -NoProfile -ExecutionPolicy Bypass -Command "Copy-Item -LiteralPath '%DIST_DIR%\SAP_Schedule_Admin.exe' -Destination '%DIST_DIR%\SAP定时任务集中管理.exe' -Force"
echo   %DIST_DIR%\SAP定时任务集中管理.exe
endlocal
