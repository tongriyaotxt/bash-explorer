@echo off
chcp 65001 >nul
rem xg.cmd - launch the graphical shell browser at the current directory
setlocal
set "_EXE=%~dp0xg.exe"
if not exist "%_EXE%" (
  echo [xg] not built: %_EXE%
  pause
  exit /b 1
)
if "%~1"=="" (
  start "" "%_EXE%" "%CD%"
) else (
  start "" "%_EXE%" "%~1"
)
exit /b 0