# يفعّل نشر التحديثات تلقائيًا على جهاز السيرفر: مهمة مجدولة تفحص GitHub كل بضع دقائق وتنشر آخر بناء ناجح (لا تنزيل ولا نقر عليك بعدها).
# مرة واحدة على السيرفر. يحتاج «مفتاح قراءة فقط» من GitHub: Settings ← Developer settings ← Fine-grained tokens،
# اختر هذا المستودع فقط وصلاحية Actions: Read-only. يُحفظ المفتاح مشفّرًا بحساب الجهاز ولا يقرؤه إلا المدير.
param(
    [string]$Owner = 'Mohammedtarq',
    [string]$Repo = 'ERP-WaterFactory',
    [string]$Branch = 'claude/erp-water-factory-basra-4q9k4l',
    [int]$EveryMinutes = 15
)
$ErrorActionPreference = 'Stop'
$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-Owner', $Owner, '-Repo', $Repo, '-Branch', "`"$Branch`"", '-EveryMinutes', $EveryMinutes)
    exit
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dir = Join-Path $env:ProgramData 'ERP-WaterFactory'
New-Item $dir -ItemType Directory -Force | Out-Null

$secure = Read-Host 'الصق مفتاح GitHub (قراءة فقط) ثم Enter' -AsSecureString
$token = (New-Object System.Net.NetworkCredential('', $secure)).Password.Trim()
if (-not $token) { throw 'لم يُدخل مفتاح' }
try {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/actions/workflows/build.yml/runs?per_page=1" -UseBasicParsing `
        -Headers @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'ERP-AutoPublish' } | Out-Null
} catch { throw "المفتاح لا يصل إلى $Owner/$Repo أو صلاحيته ناقصة (يلزم Actions: Read-only): $($_.Exception.Message)" }

# المفتاح مشفّر بحساب الجهاز (يفكّه SYSTEM الذي يشغّل المهمة) ومقيَّد على SYSTEM والمديرين فقط
Add-Type -AssemblyName System.Security
$tokenFile = Join-Path $dir 'github-token.bin'
[IO.File]::WriteAllBytes($tokenFile, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($token), $null, 'LocalMachine'))
& icacls $tokenFile /inheritance:r /grant '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $dir 'autopublish.json'), (ConvertTo-Json @{ owner = $Owner; repo = $Repo; branch = $Branch } -Compress), $utf8)
Copy-Item (Join-Path $PSScriptRoot 'Check-GitHubUpdate.ps1') (Join-Path $dir 'Check-GitHubUpdate.ps1') -Force

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dir 'Check-GitHubUpdate.ps1')`""
# ثلاثة مشغّلات: كل بضع دقائق (بمدة صريحة وإلا لا يتكرر على بعض إصدارات Windows)، وعند تشغيل الجهاز، وعند دخول المستخدم
# (بعد دقيقة) — فلا يفوتها شيء بعد إغلاق الجهاز أو سكونه ليلًا. والمهمة توقظ الجهاز من السكون إن أمكن.
$tRepeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes) -RepetitionDuration (New-TimeSpan -Days 3650)
$tBoot = New-ScheduledTaskTrigger -AtStartup; $tBoot.Delay = 'PT1M'
$tLogon = New-ScheduledTaskTrigger -AtLogOn; $tLogon.Delay = 'PT1M'
$trigger = @($tRepeat, $tBoot, $tLogon)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
Register-ScheduledTask -TaskName 'ERP-AutoPublish' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "تم: يفحص السيرفر GitHub كل $EveryMinutes دقيقة وينشر آخر بناء ناجح وحده." -ForegroundColor Green

Write-Host "فحص أول الآن..."
& (Join-Path $dir 'Check-GitHubUpdate.ps1')
Write-Host "السجل: $(Join-Path $dir 'autopublish.log')   |   لإيقافه: Unregister-ScheduledTask ERP-AutoPublish" -ForegroundColor Cyan
