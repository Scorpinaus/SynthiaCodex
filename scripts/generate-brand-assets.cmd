@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0generate-brand-assets.ps1" %*
exit /b %ERRORLEVEL%
