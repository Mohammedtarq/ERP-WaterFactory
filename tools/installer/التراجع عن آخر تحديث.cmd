@echo off
rem Rolls the server back to the previously published version (emergency use)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Rollback-Update.ps1" %*
pause
