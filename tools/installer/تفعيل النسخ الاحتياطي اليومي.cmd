@echo off
rem One-time on the SERVER PC: daily SQL Server backups to C:\ERP-Backup (keeps 14 days)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Backup.ps1" %*
pause
