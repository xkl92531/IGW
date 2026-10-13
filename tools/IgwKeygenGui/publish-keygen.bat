@echo off
rem Double-click: set the admin password, embed the encrypted private key, output to installer\out
cd /d "%~dp0..\.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if not errorlevel 1 explorer "%~dp0..\..\installer\out"
echo.
pause
