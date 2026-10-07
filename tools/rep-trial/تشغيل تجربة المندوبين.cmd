@echo off
rem Rep app trial over the factory Wi-Fi (temporary relay server + sync)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-RepTrial.ps1"
pause
