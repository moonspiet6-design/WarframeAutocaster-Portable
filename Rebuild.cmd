@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Portable.ps1" %*
set "buildStatus=%ERRORLEVEL%"
if not "%buildStatus%"=="0" pause
exit /b %buildStatus%
