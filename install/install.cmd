@echo off
:: Double-click to install. Asks for admin rights and runs install.ps1 in an admin window, then starts the tray app
:: from this window, which has no admin rights: the tray must never run elevated.
if "%~1"=="/elevated" goto run
fltmc >nul 2>&1 && goto run
set "CM_SELF=%~f0"
powershell -NoProfile -Command "$p = Start-Process -FilePath $env:CM_SELF -ArgumentList '/elevated' -Verb RunAs -Wait -PassThru; exit [int]($p.ExitCode -ne 0)" || goto failed
:: Only once the service runs. The Program Files known folder comes from the registry value install.ps1 reads, not %ProgramFiles%.
powershell -NoProfile -Command "$tray = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ConnectionManager\ConnectionManager.exe'; if ((Get-Service ConnectionManager -ErrorAction SilentlyContinue).Status -ne 'Running' -or -not (Test-Path -LiteralPath $tray -PathType Leaf)) { exit 1 }; Start-Process -FilePath $tray" || goto failed
exit /b 0
:failed
echo Connection Manager was not installed, or its service is not running. The admin window showed why.
pause
exit /b 1
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set "CM_RESULT=%errorlevel%"
:: Started from an admin prompt, there is no window without admin rights to start the tray from.
if not "%~1"=="/elevated" if "%CM_RESULT%"=="0" echo Start Connection Manager from the Start menu, or sign out and in again.
pause
exit /b %CM_RESULT%
