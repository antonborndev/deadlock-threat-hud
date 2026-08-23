@echo off
setlocal

echo Building threathud directly to Deadlock Addons...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_mod.ps1" -ModFolderName "threathud" -Force

set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo ThreatHud build finished.
) else (
    echo ThreatHud build failed with exit code %EXIT_CODE%.
)

pause
endlocal & exit /b %EXIT_CODE%