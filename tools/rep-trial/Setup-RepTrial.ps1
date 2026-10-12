# إعداد تجربة المندوبين بنقرتين على الحاسبة نفسها (الحاسبة هي الخادم، والهاتف على واي فاي الشبكة نفسها).
# يفعل ما يلزم تلقائيًا ثم يشغّل الخادم الوسيط وخدمة المزامنة في هذه النافذة. أغلق النافذة لإيقاف التجربة.
# التفاصيل: docs/rep-app.md
param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$agent = Join-Path (Split-Path $here -Parent) 'SyncAgent\ERP.SyncAgent.exe'
$ruleName = 'ERP Rep Trial'

function Step([string]$text) { Write-Host ''; Write-Host "◼ $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  ✔ $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  ! $text" -ForegroundColor Yellow }
function Stop-Here([string]$text) { Write-Host ''; Write-Host $text -ForegroundColor Red; Read-Host 'اضغط Enter للإغلاق'; exit 1 }

try {
    # 1) صلاحية المسؤول (لجدار الحماية وتعريف الشبكة): نافذة موافقة ويندوز
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'سيطلب ويندوز إذن المسؤول لفتح المنفذ وتعريف الشبكة — اضغط «نعم».' -ForegroundColor Yellow
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Port $Port"
        exit 0
    }

    # 2) قاعدة الخادم الوسيط: SQL Server Express
    Step 'التحقق من SQL Server Express'
    $svc = Get-Service -Name 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue
    if (-not $svc) { Stop-Here 'لم أجد SQL Server Express (الخدمة MSSQL$SQLEXPRESS). الخادم الوسيط يحتاجه؛ ثبّته أو أخبرني لأجد بديلًا.' }
    if ($svc.Status -ne 'Running') { Start-Service $svc; Ok 'شُغّلت خدمة SQL Server Express' } else { Ok 'يعمل' }

    # 3) شبكة الحاسبة: عنوانها والشبكة التي لها بوابة (الراوتر)
    Step 'تحديد شبكة الواي فاي والعنوان'
    $cfg = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } | Select-Object -First 1
    if (-not $cfg) { Stop-Here 'الحاسبة غير متصلة بشبكة (لا راوتر). اتصل بواي فاي البيت ثم أعد تشغيل هذا الملف.' }
    $ip = $cfg.IPv4Address.IPAddress
    Ok "العنوان: $ip  (الشبكة: $($cfg.InterfaceAlias))"

    # 4) الشبكة «خاصة» كي يسمح الجدار الناري بدخول الهاتف
    $netProfile = Get-NetConnectionProfile -InterfaceIndex $cfg.InterfaceIndex -ErrorAction SilentlyContinue
    if ($netProfile -and $netProfile.NetworkCategory -eq 'Public') {
        Set-NetConnectionProfile -InterfaceIndex $cfg.InterfaceIndex -NetworkCategory Private
        Ok "جُعلت الشبكة «$($netProfile.Name)» خاصة (كانت عامة)"
    } else { Ok 'الشبكة ليست عامة' }

    # 5) فتح منفذ التجربة
    Step "فتح المنفذ $Port في جدار الحماية"
    $rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if (-not $rule) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Private | Out-Null
        Ok 'أُضيفت القاعدة'
    } else { Ok 'القاعدة موجودة' }

    # 6) مفتاح المعمل: يُقرأ من البرنامج ويوضع في الخادم الوسيط تلقائيًا
    Step 'قراءة مفتاح المعمل'
    if (-not (Test-Path $agent)) { Stop-Here "لم أجد خدمة المزامنة: $agent" }
    $ErrorActionPreference = 'Continue'   # في ويندوز PowerShell 5.1 يحوّل 2>&1 سطور الخطأ الأصلية إلى استثناء إن كانت Stop
    $out = @(& $agent --print-key 2>&1)
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $key = $out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | Select-Object -First 1
    $errText = ($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() }) -join "`n"
    if ($code -ne 0 -or [string]::IsNullOrWhiteSpace([string]$key)) {
        $why = if ($errText) { $errText } else { 'لا يوجد مفتاح للمعمل بعد.' }
        Stop-Here "$why`nإن لم يكن المفتاح مولَّدًا: «الإعدادات ← المزامنة السحابية ← توليد مفتاح جديد» في البرنامج (لا حاجة لنسخه)، ثم أعد تشغيل هذا الملف."
    }
    $key = ([string]$key).Trim()
    Set-Content -Path (Join-Path $here 'agent-key.txt') -Value $key -Encoding ASCII
    Ok 'وُضع المفتاح في الخادم الوسيط'

    Write-Host ''
    Write-Host '────────────────────────────────────────────' -ForegroundColor DarkGray
    Write-Host 'الباقي في البرنامج (مرة واحدة):' -ForegroundColor White
    Write-Host ' 1) «الإعدادات ← المزامنة السحابية»: الصق العنوان (نُسخ إلى الحافظة)، فعّل المزامنة، احفظ، ثم «اختبار الاتصال».' -ForegroundColor White
    Write-Host ' 2) «المندوبون ← أجهزة التطبيق»: اختر المندوب، سجّل الهاتف، ثم صوّر الرمز من التطبيق.' -ForegroundColor White
    Write-Host " اختبار الهاتف قبل الربط: افتح في متصفحه  http://${ip}:$Port/api/health" -ForegroundColor White
    Write-Host '────────────────────────────────────────────' -ForegroundColor DarkGray

    # 7) تشغيل الخادم الوسيط والمزامنة (تطبع العنوان وتنسخه وتبقى مفتوحة)
    & (Join-Path $here 'Start-RepTrial.ps1') -Port $Port -Ip $ip
}
catch {
    Stop-Here "تعذّر الإكمال: $($_.Exception.Message)"
}
