@echo off
rem One-click setup for the rep app trial (this PC is the server)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-RepTrial.ps1"
pause
