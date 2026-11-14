@echo off
rem Water factory ERP installer launcher
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ERP.ps1" -Mode Trainee
pause
