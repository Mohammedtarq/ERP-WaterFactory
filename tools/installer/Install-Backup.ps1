# يفعّل النسخ الاحتياطي اليومي على السيرفر (مرة واحدة): مجلد C:\ERP-Backup ومهمة مجدولة 2:00 فجرًا، ويجرّب نسخة أولى الآن.
param(
    [string]$Instance = 'localhost\SQLEXPRESS',
    [string]$Root = 'C:\ERP-Backup',
    [int]$KeepDays = 14,
    [string]$At = '02:00'
)
$ErrorActionPreference = 'Stop'
$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-Instance', "`"$Instance`"", '-Root', "`"$Root`"", '-KeepDays', $KeepDays, '-At', $At)
    exit
}
$dir = Join-Path $env:ProgramData 'ERP-WaterFactory'
New-Item $dir -ItemType Directory -Force | Out-Null
New-Item $Root -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Backup-ERP.ps1') (Join-Path $dir 'Backup-ERP.ps1') -Force

# المجلد مقيَّد على النظام والمديرين وخدمة SQL (تكتب الملف) فقط: النسخ فيها بيانات مالية
$svc = 'NT SERVICE\MSSQL$' + ($Instance -replace '^.*\\', '')
& icacls $Root /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${svc}:(OI)(CI)M" | Out-Null

# حساب SYSTEM (الذي يشغّل المهمة) يحتاج صلاحية على SQL للنسخ
& sqlcmd -S $Instance -E -b -Q "IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT AUTHORITY\SYSTEM') CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS; ALTER SERVER ROLE sysadmin ADD MEMBER [NT AUTHORITY\SYSTEM];"
if ($LASTEXITCODE -ne 0) { throw 'تعذّر منح صلاحية النسخ للمهمة على SQL Server' }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dir 'Backup-ERP.ps1')`" -Instance `"$Instance`" -Root `"$Root`" -KeepDays $KeepDays"
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
Register-ScheduledTask -TaskName 'ERP-DailyBackup' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "تم: نسخ احتياطي يومي الساعة $At في $Root (يحتفظ بآخر $KeepDays يومًا)." -ForegroundColor Green

Write-Host 'نسخة أولى الآن...'
& (Join-Path $dir 'Backup-ERP.ps1') -Instance $Instance -Root $Root -KeepDays $KeepDays
Write-Host "السجل: $Root\backup.log   |   لإيقافه: Unregister-ScheduledTask ERP-DailyBackup" -ForegroundColor Cyan
