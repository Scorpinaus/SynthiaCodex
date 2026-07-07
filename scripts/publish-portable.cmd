@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-portable.ps1" %*
exit /b %ERRORLEVEL%
