@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-android.ps1" %*
exit /b %errorlevel%
