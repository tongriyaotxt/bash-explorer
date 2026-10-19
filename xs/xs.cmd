@echo off
chcp 65001 >nul
rem xs.cmd - run xs in THIS terminal, starting at the current directory
"%~dp0xs.exe" %*
