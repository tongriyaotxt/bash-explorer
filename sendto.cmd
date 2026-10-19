@echo off
chcp 65001 >nul
rem sendto.cmd - put a shortcut of this file into shell:sendto
rem Select items in Explorer -> right click -> Send to -> xf
rem Then run:  xf          (REPL)  or  xf sh "wc -l @*"
setlocal
set "_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
"%_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0xf.ps1" from %*
exit /b %ERRORLEVEL%
