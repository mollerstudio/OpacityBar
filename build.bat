@echo off
rem Double-click build: runs build.ps1 (app, plus the installer when the helper DLLs are in dist\).
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if %ERRORLEVEL% NEQ 0 (
    echo Build failed.
    pause
    exit /b %ERRORLEVEL%
)
echo Done: %~dp0dist
exit /b 0
