@echo off
chcp 65001 >nul
rem xg.cmd - launch the graphical shell browser
rem   xg        -> "This PC" (drive list; does not start on C:)
rem   xg .      -> current directory
rem   xg PATH   -> that location
rem ASCII only: cmd parses .cmd under the caller's codepage, so no CJK here.
setlocal
set "_EXE=%~dp0xg.exe"
if not exist "%_EXE%" (
  echo [xg] not built: %_EXE%
  pause
  exit /b 1
)
if "%~1"=="" (
  start "" "%_EXE%"
) else (
  start "" "%_EXE%" %*
)
exit /b 0
