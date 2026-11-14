# يطبّق تحديث البرنامج من السيرفر (يشغّله البرنامج نفسه بعد موافقة المستخدم ثم يغلق):
#   ينتظر إغلاق البرنامج ← ينزّل من السيرفر الملفات المتغيّرة فقط ← يتحقق من بصماتها ← يحفظ نسخة احتياطية مما سيُستبدل
#   ← يستبدل ← يعيد تشغيل البرنامج. أي فشل: يعيد الملفات القديمة كما كانت ويفتح البرنامج بنسخته السابقة.
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$InstallDir,
    [int]$ProcessId = 0,
    [switch]$NoLaunch,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$BaseUrl = $BaseUrl.TrimEnd('/') + '/'
$InstallDir = $InstallDir.TrimEnd('\')
$log = Join-Path $env:TEMP 'ERP-Update.log'
Start-Transcript -Path $log -Append | Out-Null

function Show-Message($text, $icon) { try { (New-Object -ComObject WScript.Shell).Popup($text, 0, 'تحديث نظام معمل المياه', $icon) | Out-Null } catch { } }
function Test-Writable($dir) {
    try { $t = Join-Path $dir ('.w' + [guid]::NewGuid().ToString('N')); [IO.File]::WriteAllText($t, 'x'); Remove-Item $t -Force; return $true } catch { return $false }
}

$services = @('ERPCloudSync', 'ERPLocalRelay') | Where-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue }
$isAdmin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
# جهاز السيرفر (له خدمتان تقفلان ملفاتهما) أو مجلد لا يُكتب فيه: يلزم صلاحية المدير مرة واحدة
if (-not $isAdmin -and -not $Elevated -and ($services.Count -gt 0 -or -not (Test-Writable $InstallDir))) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`"", '-BaseUrl', "`"$BaseUrl`"", '-InstallDir', "`"$InstallDir`"", '-ProcessId', $ProcessId, '-Elevated')
    if ($NoLaunch) { $a += '-NoLaunch' }
    Stop-Transcript | Out-Null
    Start-Process powershell -Verb RunAs -ArgumentList $a
    exit
}

$backup = Join-Path $env:ProgramData 'ERP-WaterFactory\previous'
$done = New-Object System.Collections.ArrayList
$stopped = @()
$ok = $false
$failure = $null
try {
    if ($ProcessId -gt 0) { try { Wait-Process -Id $ProcessId -Timeout 90 -ErrorAction Stop } catch { } }
    # JSON يُنزَّل كملف ويُقرأ UTF-8 صراحةً (لا نعتمد على نوع المحتوى ولا ترميز PowerShell 5.1)
    function Get-Json($name) {
        $f = Join-Path $env:TEMP ('erp-' + $name)
        Invoke-WebRequest -Uri ($BaseUrl + $name) -OutFile $f -UseBasicParsing
        return [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    $manifest = Get-Json 'manifest.json'
    $offer = Get-Json 'version.json'

    # المتغيّر فقط: ما اختلفت بصمته أو لم يكن موجودًا
    $changed = @()
    foreach ($m in @($manifest)) {
        $dst = Join-Path $InstallDir ($m.path.Replace('/', '\'))
        if ((Test-Path -LiteralPath $dst) -and ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -eq $m.sha256)) { continue }
        $changed += $m
    }
    Write-Host "الملفات المتغيّرة: $($changed.Count) من $(@($manifest).Count)"

    # 1) التنزيل إلى مجلد مؤقت والتحقق من البصمة قبل لمس البرنامج
    $stage = Join-Path $env:TEMP ('erp-update-' + $offer.version)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item $stage -ItemType Directory -Force | Out-Null
    foreach ($m in $changed) {
        $segments = $m.path.Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }
        $tmp = Join-Path $stage ($m.path.Replace('/', '\'))
        New-Item (Split-Path $tmp -Parent) -ItemType Directory -Force | Out-Null
        Invoke-WebRequest -Uri ($BaseUrl + 'files/' + ($segments -join '/')) -OutFile $tmp -UseBasicParsing
        if ((Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash -ne $m.sha256) { throw "الملف $($m.path) وصل ناقصًا أو تالفًا. أعد المحاولة" }
    }

    # 2) إيقاف خدمات السيرفر (إن وُجدت) كي لا تُقفل ملفاتها
    foreach ($s in $services) { Stop-Service -Name $s -Force -ErrorAction Stop; $stopped += $s }

    # 3) نسخة احتياطية مما سيُستبدل ثم الاستبدال
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    New-Item $backup -ItemType Directory -Force | Out-Null
    foreach ($m in $changed) {
        $rel = $m.path.Replace('/', '\')
        $dst = Join-Path $InstallDir $rel
        $existed = Test-Path -LiteralPath $dst
        if ($existed) {
            $b = Join-Path $backup $rel
            New-Item (Split-Path $b -Parent) -ItemType Directory -Force | Out-Null
            Copy-Item -LiteralPath $dst -Destination $b -Force
        }
        [void]$done.Add([pscustomobject]@{ Rel = $rel; Existed = $existed })
        New-Item (Split-Path $dst -Parent) -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $stage $rel) -Destination $dst -Force
    }
    $ok = $true
    Write-Host "اكتمل التحديث إلى $($offer.version)"
}
catch {
    $failure = $_.Exception.Message
    Write-Host "فشل التحديث: $failure" -ForegroundColor Red
    # التراجع: الملفات المستبدلة تعود، والمضافة حديثًا تُحذف
    foreach ($d in $done) {
        $dst = Join-Path $InstallDir $d.Rel
        try {
            if ($d.Existed) { Copy-Item -LiteralPath (Join-Path $backup $d.Rel) -Destination $dst -Force }
            elseif (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force }
        } catch { Write-Host "تعذّر إرجاع $($d.Rel): $($_.Exception.Message)" -ForegroundColor Red }
    }
}
finally {
    foreach ($s in $stopped) { try { Start-Service -Name $s } catch { Write-Host "تعذّر تشغيل الخدمة ${s}: $($_.Exception.Message)" } }
    Stop-Transcript | Out-Null
}
if (-not $ok -and -not $NoLaunch) { Show-Message "تعذّر تحديث البرنامج:`n$failure`n`nأُعيدت النسخة السابقة وسيفتح البرنامج بها. أرسل الملف $log للدعم الفني." 16 }
if (-not $NoLaunch) { Start-Process (Join-Path $InstallDir 'ERP.Desktop.exe') -WorkingDirectory $InstallDir }
if (-not $ok) { exit 1 }
