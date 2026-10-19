@echo off
chcp 65001 >nul
rem xf.cmd - entry point. usage: xf | xf sel | xf sh "cmd" | xf here | xf find "*.md"
setlocal
set "_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
"%_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0xf.ps1" %*
exit /b %ERRORLEVEL%
