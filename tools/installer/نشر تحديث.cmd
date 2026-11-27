@echo off
rem Publishes the package in this folder to the server so every PC updates itself (add -Optional for a cosmetic-only update)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Update.ps1" %*
pause
