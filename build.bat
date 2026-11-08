@echo off
setlocal
echo ========================================================
echo   OPACBAR - Standalone Windows Build
echo ========================================================
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed.
    exit /b %ERRORLEVEL%
)
echo [SUCCESS] Standalone binary ready at: %~dp0dist\Opacbar.exe
exit /b 0
