# يفحص GitHub عن آخر بناء ناجح ويُنزّل حزمته وينشرها على السيرفر وحدها (تشغّله مهمة مجدولة كل بضع دقائق، يثبّتها Install-AutoPublish.ps1).
# يحتاج مفتاح قراءة فقط محفوظًا مشفّرًا بحساب الجهاز. لا يفعل شيئًا إن لم يظهر بناء جديد ناجح.
# أضف [optional] في رسالة دفعة GitHub ليُنشر التحديث اختياريًا (الافتراضي إجباري).
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dir = Join-Path $env:ProgramData 'ERP-WaterFactory'
$log = Join-Path $dir 'autopublish.log'
function Log($m) {
    try {
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 200000) { Remove-Item $log -Force }
        Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format s), $m) -Encoding UTF8
    } catch { }
    Write-Host $m
}

$work = $null
try {
    $cfg = [IO.File]::ReadAllText((Join-Path $dir 'autopublish.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
    Add-Type -AssemblyName System.Security
    $token = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $dir 'github-token.bin')), $null, 'LocalMachine'))
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'ERP-AutoPublish' }
    $api = "https://api.github.com/repos/$($cfg.owner)/$($cfg.repo)"

    $runs = Invoke-RestMethod -Uri "$api/actions/workflows/build.yml/runs?branch=$([uri]::EscapeDataString($cfg.branch))&status=success&per_page=10" -Headers $headers
    $run = @($runs.workflow_runs) | Sort-Object created_at -Descending | Select-Object -First 1
    if (-not $run) { Log "لا يوجد بناء ناجح على الفرع $($cfg.branch) بعد"; return }

    $statePath = Join-Path $dir 'autopublish-state.json'
    $last = if (Test-Path $statePath) { ([IO.File]::ReadAllText($statePath) | ConvertFrom-Json).runId } else { 0 }
    if (-not $Force -and $last -eq $run.id) { return }   # لا جديد

    $artifacts = Invoke-RestMethod -Uri "$api/actions/runs/$($run.id)/artifacts?per_page=50" -Headers $headers
    $art = @($artifacts.artifacts) | Where-Object { $_.name -eq 'ERP-WaterFactory-win-x64' -and -not $_.expired } | Select-Object -First 1
    if (-not $art) { Log "البناء $($run.run_number) بلا حزمة Windows صالحة (انتهت مدتها؟)"; return }

    Log "بناء جديد ناجح رقم $($run.run_number): تنزيل الحزمة..."
    # الرابط يحوّل لمخزن مؤقت موقّع: نأخذ العنوان بالمفتاح ثم ننزّل بدونه
    # مهلة طويلة + ثلاث محاولات (انقطاع الاتصال المؤقت لا يُفشل الفحص)
    $location = $null
    for ($try = 1; $try -le 3 -and -not $location; $try++) {
        try {
            $req = [Net.HttpWebRequest]::Create($art.archive_download_url)
            $req.AllowAutoRedirect = $false
            $req.Timeout = 300000
            $req.UserAgent = 'ERP-AutoPublish'
            $req.Headers.Add('Authorization', "Bearer $token")
            $resp = $req.GetResponse()
            $location = $resp.Headers['Location']
            $resp.Close()
        } catch { Log "محاولة $try تعذّرت: $($_.Exception.Message)"; if ($try -lt 3) { Start-Sleep -Seconds (10 * $try) } }
    }
    if (-not $location) { throw "لم يُعطَ رابط تنزيل الحزمة (انقطاع اتصال أو صلاحية المفتاح: Actions قراءة)" }

    $work = Join-Path $env:TEMP ('erp-autopublish-' + $run.id)
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item $work -ItemType Directory -Force | Out-Null
    $zip = Join-Path $work 'pkg.zip'
    Invoke-WebRequest -Uri $location -OutFile $zip -UseBasicParsing -TimeoutSec 1200
    Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'pkg') -Force
    $exe = Get-ChildItem (Join-Path $work 'pkg') -Recurse -Filter 'ERP.Desktop.exe' | Select-Object -First 1
    if (-not $exe) { throw "الحزمة المنزّلة لا تحوي ERP.Desktop.exe" }
    $source = $exe.DirectoryName

    $optional = ($run.head_commit.message -match '\[optional\]')
    $publish = Join-Path $source 'Publish-Update.ps1'
    if (-not (Test-Path $publish)) { throw "الحزمة لا تحوي Publish-Update.ps1" }
    $updates = Join-Path $dir 'Updates'
    if ($optional) { & $publish -Source $source -UpdatesDir $updates -Optional } else { & $publish -Source $source -UpdatesDir $updates }

    # يُحدِّث السكربت نفسه من الحزمة المنشورة (كي تصل إصلاحاته دون تدخل يدوي)
    try { $self = Join-Path $source 'Check-GitHubUpdate.ps1'; if (Test-Path $self) { Copy-Item $self (Join-Path $dir 'Check-GitHubUpdate.ps1') -Force } } catch { }
    [IO.File]::WriteAllText($statePath, (ConvertTo-Json @{ runId = $run.id; runNumber = $run.run_number; publishedAt = (Get-Date).ToString('s') } -Compress))
    Log "نُشر التحديث (بناء $($run.run_number)) $(if ($optional) { 'اختياريًا' } else { 'إجباريًا' }) — تتحدث الأجهزة عند فتح البرنامج"
}
catch { Log "تعذّر الفحص/النشر: $($_.Exception.Message)" }
finally { if ($work -and (Test-Path $work)) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } }
