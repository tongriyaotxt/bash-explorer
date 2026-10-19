@echo off
chcp 65001 >nul
setlocal
set "_EXE=%LOCALAPPDATA%\xf\app\xs\xs.exe"
if not exist "%_EXE%" (
  echo [xsw] not found: %_EXE%
  echo run setup.ps1 first.
  pause
  exit /b 1
)
set "_D=%~1"
if "%_D%"=="" set "_D=%CD%"
if not exist "%_D%" (
  echo [xsw] no such directory: %_D%
  pause
  exit /b 1
)
start "" wt.exe -d "%_D%" "%_EXE%"
exit /b 0