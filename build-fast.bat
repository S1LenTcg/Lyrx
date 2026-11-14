@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%build.ps1" -NoSingleFile -NoSelfContained -NoRestore -OutputDir "%SCRIPT_DIR%dist-fast" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo %cmdcmdline% | findstr /i /c:"\"\"" >nul 2>&1
if "%ERRORLEVEL%"=="0" (
    echo.
    pause
)

exit /b %EXIT_CODE%
