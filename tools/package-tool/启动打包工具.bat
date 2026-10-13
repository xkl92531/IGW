@echo off
rem IGW Package Tool launcher (ASCII only to avoid codepage issues)
cd /d "%~dp0"
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0IGW.PackageTool.ps1"
