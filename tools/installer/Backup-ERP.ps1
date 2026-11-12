# نسخ احتياطي يومي لقواعد النظام (ERP_*) على السيرفر: ملف .bak لكل قاعدة في مجلد اليوم، مع فحص سلامته، وحذف ما هو أقدم من عدد أيام محدد.
# يشغّله Install-Backup.ps1 كمهمة مجدولة يوميًا. يمكن تشغيله يدويًا أيضًا.
param(
    [string]$Instance = 'localhost\SQLEXPRESS',
    [string]$Root = 'C:\ERP-Backup',
    [int]$KeepDays = 14
)
$ErrorActionPreference = 'Stop'
New-Item $Root -ItemType Directory -Force | Out-Null
$logFile = Join-Path $Root 'backup.log'
function Log($m) { $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m"; Add-Content -Path $logFile -Value $line -Encoding UTF8; Write-Host $line }

$names = & sqlcmd -S $Instance -E -h -1 -W -Q "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name LIKE 'ERP[_]%' AND state_desc = 'ONLINE'"
if ($LASTEXITCODE -ne 0) { Log "FAIL: cannot read databases from $Instance"; exit 1 }
$dbs = @($names | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
if ($dbs.Count -eq 0) { Log "FAIL: no ERP_* databases found on $Instance"; exit 1 }

$day = Join-Path $Root (Get-Date -Format 'yyyy-MM-dd')
New-Item $day -ItemType Directory -Force | Out-Null
$stamp = Get-Date -Format 'HHmm'
$failed = 0
foreach ($db in $dbs) {
    $file = Join-Path $day "$db`_$stamp.bak"
    & sqlcmd -S $Instance -E -b -Q "BACKUP DATABASE [$db] TO DISK = N'$file' WITH INIT, CHECKSUM, COMPRESSION, NAME = N'$db'" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # بعض الإصدارات لا تدعم الضغط: نعيد بلا ضغط
        & sqlcmd -S $Instance -E -b -Q "BACKUP DATABASE [$db] TO DISK = N'$file' WITH INIT, CHECKSUM, NAME = N'$db'" | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { $failed++; Log "FAIL: backup of $db"; continue }
    & sqlcmd -S $Instance -E -b -Q "RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM" | Out-Null
    if ($LASTEXITCODE -ne 0) { $failed++; Log "FAIL: verify of $db"; continue }
    Log ("OK: {0} ({1:N1} MB)" -f $db, ((Get-Item $file).Length / 1MB))
}

# لا يُحذف القديم إلا إذا نجحت نسخة اليوم كلها
if ($failed -eq 0) {
    $limit = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem $Root -Directory | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}$' -and $_.LastWriteTime -lt $limit } | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        Log "Removed old backup folder $($_.Name)"
    }
}
if ($failed) { exit 1 }
