@echo off
rem One-time on the SERVER PC: publishes every successful GitHub build automatically (needs a read-only GitHub token)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AutoPublish.ps1" %*
pause
