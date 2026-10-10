@echo off
:: Double-click to uninstall (keeps settings). Asks for admin rights, then runs uninstall.ps1.
if "%~1"=="/elevated" goto run
fltmc >nul 2>&1 && goto run
set "CM_SELF=%~f0"
powershell -NoProfile -Command "Start-Process -FilePath $env:CM_SELF -ArgumentList '/elevated' -Verb RunAs" || pause
exit /b
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
