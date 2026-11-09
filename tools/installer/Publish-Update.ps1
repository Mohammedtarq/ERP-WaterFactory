# ينشر حزمة البرنامج الجديدة على السيرفر ليحدّث كل جهاز نفسه عند فتح البرنامج (الخادم المحلي يقدّمها على المنفذ 5080).
# الاستخدام على جهاز السيرفر: نزّل الحزمة الجديدة (مجلد ERP-WaterFactory-win-x64) وانقر مرتين على «نشر تحديث.cmd» بداخلها.
#   -Optional : تحديث اختياري (تغييرات شكلية). الافتراضي إجباري لأن قاعدة البيانات قد تتغيّر ولا تعمل نسخة قديمة معها.
param(
    [string]$Source,
    [string]$UpdatesDir = (Join-Path $env:ProgramData 'ERP-WaterFactory\Updates'),
    [switch]$Optional
)
$ErrorActionPreference = 'Stop'

# الكتابة في %ProgramData% تحتاج صلاحية المدير: يعيد تشغيل نفسه بها (إلا لو حُدّد مجلد آخر كما في الاختبار الآلي)
$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and $PSBoundParameters.ContainsKey('UpdatesDir') -eq $false) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    if ($Source) { $a += @('-Source', "`"$Source`"") }
    if ($Optional) { $a += '-Optional' }
    Start-Process powershell -Verb RunAs -ArgumentList $a
    exit
}

if (-not $Source) { $Source = $PSScriptRoot }
if (-not (Test-Path (Join-Path $Source 'ERP.Desktop.exe'))) { throw "لم يُعثر على ERP.Desktop.exe في $Source. شغّل السكربت من داخل مجلد الحزمة الجديدة." }
$applyScript = Join-Path $Source 'Apply-Update.ps1'
if (-not (Test-Path $applyScript)) { throw "ينقص Apply-Update.ps1 في الحزمة" }

$version = (Get-Item (Join-Path $Source 'ERP.Desktop.exe')).VersionInfo.FileVersion
if (-not $version -or $version -eq '1.0.0.0') { Write-Host "  [!] رقم النسخة 1.0.0.0 (بناء محلي): لن يُعدّ تحديثًا لمن عنده النسخة نفسها" -ForegroundColor Yellow }

$next = Join-Path $UpdatesDir 'next'
$current = Join-Path $UpdatesDir 'current'
$old = Join-Path $UpdatesDir 'old'
foreach ($d in $next, $old) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
$files = Join-Path $next 'files'
New-Item $files -ItemType Directory -Force | Out-Null

Write-Host "  نسخ الحزمة ($version) ..."
& robocopy $Source $files /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP /XF 'Install-ERP.ps1' 'Publish-Update.ps1' 'Apply-Update.ps1' '*.cmd' | Out-Null
if ($LASTEXITCODE -ge 8) { throw "تعذّر نسخ الحزمة (robocopy $LASTEXITCODE)" }
Copy-Item $applyScript (Join-Path $next 'Apply-Update.ps1') -Force

# بيان الملفات: المسار النسبي وبصمته؛ منه يعرف كل جهاز ما تغيّر فينزّله فقط ويتحقق منه بعد التنزيل
$prefix = $files.TrimEnd('\') + '\'
$entries = foreach ($f in Get-ChildItem $files -Recurse -File) {
    [pscustomobject]@{ path = $f.FullName.Substring($prefix.Length).Replace('\', '/'); sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash; size = $f.Length }
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $next 'manifest.json'), (ConvertTo-Json -InputObject @($entries) -Compress), $utf8)
$offer = [ordered]@{ version = $version; mandatory = (-not $Optional); publishedAt = (Get-Date).ToString('s'); files = @($entries).Count }
# version.json آخرًا: هو ما يفحصه البرنامج، فلا يرى أحد نشرًا ناقصًا
[IO.File]::WriteAllText((Join-Path $next 'version.json'), (ConvertTo-Json -InputObject $offer -Compress), $utf8)

if (Test-Path $current) { Rename-Item $current 'old' }
Rename-Item $next 'current'
if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "تم نشر النسخة $version ($(@($entries).Count) ملفًا) — $(if ($Optional) { 'اختياري' } else { 'إجباري' }). ستتحدث الأجهزة عند فتح البرنامج." -ForegroundColor Green
