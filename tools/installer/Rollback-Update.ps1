# يتراجع عن آخر تحديث نُشر على هذا السيرفر: ينشر النسخة السابقة المحفوظة (Updates\old) فتعود إليها الأجهزة عند فتح البرنامج.
# للطوارئ فقط: النسخة القديمة مع قاعدة بيانات حدّثتها نسخة أحدث تعمل غالبًا (التحديثات تضيف ولا تحذف) لكن لا يُضمن ذلك دائمًا.
param([string]$UpdatesDir = (Join-Path $env:ProgramData 'ERP-WaterFactory\Updates'))
$ErrorActionPreference = 'Stop'

$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and $PSBoundParameters.ContainsKey('UpdatesDir') -eq $false) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"")
    exit
}

$current = Join-Path $UpdatesDir 'current'
$old = Join-Path $UpdatesDir 'old'
if (-not (Test-Path (Join-Path $old 'version.json'))) { throw 'لا توجد نسخة سابقة محفوظة للتراجع إليها (لم يُنشر تحديث بعد، أو حُذفت).' }

$utf8 = New-Object System.Text.UTF8Encoding($false)
$prev = [IO.File]::ReadAllText((Join-Path $old 'version.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$now = if (Test-Path (Join-Path $current 'version.json')) { ([IO.File]::ReadAllText((Join-Path $current 'version.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json).version } else { '?' }
Write-Host "المنشور الآن: $now  ← التراجع إلى: $($prev.version)"
$answer = Read-Host 'اكتب نعم للتأكيد'
if ($answer -ne 'نعم' -and $answer -ne 'yes') { Write-Host 'أُلغي التراجع.'; return }

# علامة rollback تجعل الأجهزة ذات النسخة الأحدث تنزل إلى هذه النسخة (بدونها لا يُعرض إلا ما هو أحدث). إجباري كي لا تبقى أجهزة على نسخ مختلفة.
$offer = [ordered]@{ version = $prev.version; mandatory = $true; rollback = $true; publishedAt = (Get-Date).ToString('s'); files = $prev.files }
[IO.File]::WriteAllText((Join-Path $old 'version.json'), (ConvertTo-Json -InputObject $offer -Compress), $utf8)

$undone = Join-Path $UpdatesDir 'undone'
if (Test-Path $undone) { Remove-Item $undone -Recurse -Force }
if (Test-Path $current) { Rename-Item $current 'undone' }
Rename-Item $old 'current'
Write-Host "تم التراجع: صارت المنشورة $($prev.version). تعود الأجهزة إليها عند فتح البرنامج." -ForegroundColor Green
