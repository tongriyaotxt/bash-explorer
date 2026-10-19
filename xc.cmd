@echo off
chcp 65001 >nul
rem xc.cmd - explorer commander. usage: xc | xc ls | xc cd .. | xc -List
setlocal
set "_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
"%_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0xc.ps1" %*
exit /b %ERRORLEVEL%
