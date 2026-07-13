@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0maintenance-sweep.ps1" %*
exit /b %ERRORLEVEL%
