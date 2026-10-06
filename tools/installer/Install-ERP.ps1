# مثبّت نظام إدارة معمل المياه — ثلاثة أوضاع:
#   Server      : SQL Server 2022 Express (يُنزَّل ويُثبَّت بصمت إن لم يكن موجودًا) + البرنامج + اختصار، ثم معالج الإعداد
#   Workstation : البرنامج + اختصار فقط (جهاز مستخدم يتصل بالسيرفر)
#   Trainee     : SQL Server LocalDB (نسخة مصغّرة) + البرنامج + قاعدة تدريب ببيانات تجريبية تلقائيًا — بلا سيرفر
# الاستخدام: انقر مرتين على «تثبيت - ….cmd» في مجلد البرنامج، أو:
#   powershell -ExecutionPolicy Bypass -File Install-ERP.ps1 -Mode Server [-EnableLan] [-SqlSetupPath D:\SQLEXPR_x64_ENU.exe]
param(
    [ValidateSet('Server', 'Workstation', 'Trainee')]
    [string]$Mode = 'Workstation',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ERP-WaterFactory'),
    [string]$SqlSetupPath,
    [switch]$EnableLan,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [تم]  $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [!]   $m" -ForegroundColor Yellow }

# ---------- صلاحية المدير (تثبيت SQL والنسخ إلى Program Files) ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Mode', $Mode, '-InstallDir', "`"$InstallDir`"")
    if ($SqlSetupPath) { $argsList += @('-SqlSetupPath', "`"$SqlSetupPath`"") }
    if ($EnableLan) { $argsList += '-EnableLan' }
    if ($NoLaunch) { $argsList += '-NoLaunch' }
    Start-Process powershell -Verb RunAs -ArgumentList $argsList
    exit
}

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'ERP.Desktop.exe'))) {
    $parent = Split-Path $source -Parent
    if (Test-Path (Join-Path $parent 'ERP.Desktop.exe')) { $source = $parent }
    else { throw "لم يُعثر على ERP.Desktop.exe بجانب المثبّت. شغّله من داخل مجلد البرنامج." }
}
$log = Join-Path $env:TEMP 'ERP-Install.log'
Start-Transcript -Path $log -Append | Out-Null
$work = Join-Path $env:TEMP 'erp-sql-setup'
New-Item $work -ItemType Directory -Force | Out-Null
$sseiUrl = 'https://go.microsoft.com/fwlink/p/?linkid=2216019'   # SQL Server 2022 Express (مثبّت Microsoft الرسمي)

function Get-Ssei {
    $ssei = Join-Path $work 'SQL2022-SSEI-Expr.exe'
    if (-not (Test-Path $ssei)) {
        Write-Host "  تنزيل مثبّت SQL Server من Microsoft..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $sseiUrl -OutFile $ssei -UseBasicParsing
    }
    return $ssei
}

function Install-SqlExpress {
    if (Get-Service -Name 'MSSQL$SQLEXPRESS' -ErrorAction SilentlyContinue) { Ok "SQL Server Express مثبّت مسبقًا (localhost\SQLEXPRESS)"; return }
    $package = $SqlSetupPath
    if (-not $package) {
        $ssei = Get-Ssei
        Write-Host "  تنزيل حزمة SQL Server Express (قد يستغرق دقائق)..."
        $p = Start-Process $ssei -ArgumentList "/ACTION=Download /MEDIAPATH=`"$work`" /MEDIATYPE=Core /QUIET" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "تعذّر تنزيل SQL Server (رمز $($p.ExitCode)). نزّل SQLEXPR_x64_ENU.exe يدويًا ومرّره بـ -SqlSetupPath" }
        $package = (Get-ChildItem $work -Filter 'SQLEXPR*.exe' | Select-Object -First 1).FullName
    }
    $extract = Join-Path $work 'extract'
    Write-Host "  فك الحزمة..."
    Start-Process $package -ArgumentList "/q /x:`"$extract`"" -Wait | Out-Null
    Write-Host "  تثبيت SQL Server Express (instance SQLEXPRESS)..."
    $setupArgs = '/Q /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=SQLEXPRESS /SQLSYSADMINACCOUNTS="BUILTIN\Administrators" ' +
                 '/TCPENABLED=1 /UPDATEENABLED=0 /IACCEPTSQLSERVERLICENSETERMS'
    $p = Start-Process (Join-Path $extract 'setup.exe') -ArgumentList $setupArgs -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "فشل تثبيت SQL Server (رمز $($p.ExitCode)). السجل: %ProgramFiles%\Microsoft SQL Server\160\Setup Bootstrap\Log" }
    Ok "ثُبّت SQL Server Express: اسم السيرفر localhost\SQLEXPRESS"
}

function Install-LocalDb {
    if (Get-Command sqllocaldb -ErrorAction SilentlyContinue) { Ok "SQL Server LocalDB مثبّت مسبقًا" }
    else {
        $ssei = Get-Ssei
        Write-Host "  تنزيل SQL Server LocalDB (حوالي 50MB)..."
        $p = Start-Process $ssei -ArgumentList "/ACTION=Download /MEDIAPATH=`"$work`" /MEDIATYPE=LocalDB /QUIET" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "تعذّر تنزيل LocalDB (رمز $($p.ExitCode)). تأكد من الاتصال بالإنترنت" }
        $msi = (Get-ChildItem $work -Filter 'SqlLocalDB*.msi' -Recurse | Select-Object -First 1).FullName
        $p = Start-Process msiexec -ArgumentList "/i `"$msi`" /qn IACCEPTSQLLOCALDBLICENSETERMS=YES" -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "فشل تثبيت LocalDB (رمز $($p.ExitCode))" }
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        Ok "ثُبّت SQL Server LocalDB"
    }
    & sqllocaldb create MSSQLLocalDB 2>$null | Out-Null
    & sqllocaldb start MSSQLLocalDB | Out-Null
    Ok "قاعدة التدريب المحلية تعمل: (localdb)\MSSQLLocalDB"
}

function Install-App {
    Write-Host "  نسخ البرنامج إلى $InstallDir ..."
    New-Item $InstallDir -ItemType Directory -Force | Out-Null
    & robocopy $source $InstallDir /E /NFL /NDL /NJH /NJS /NP /XF 'Install-ERP.ps1' '*.cmd' | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "تعذّر نسخ ملفات البرنامج (robocopy $LASTEXITCODE)" }
    $exe = Join-Path $InstallDir 'ERP.Desktop.exe'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('CommonPrograms'))) {
        $lnk = $shell.CreateShortcut((Join-Path $folder 'نظام معمل المياه.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $InstallDir
        if ($Mode -eq 'Trainee') { $lnk.Description = 'نظام معمل المياه — نسخة التدريب' }
        $lnk.Save()
    }
    Ok "ثُبّت البرنامج، واختصاره على سطح المكتب وقائمة البرامج"
    return $exe
}

try {
    Write-Host "مثبّت نظام معمل المياه — الوضع: $Mode" -ForegroundColor White
    switch ($Mode) {
        'Server' {
            Step "1) SQL Server Express"
            Install-SqlExpress
            if ($EnableLan) {
                Step "تجهيز الشبكة (TCP 1433 والجدار الناري ومستخدم erp_app)"
                & (Join-Path $source 'الشبكة\Setup-SqlServerForLan.ps1')
            }
        }
        'Trainee' {
            Step "1) SQL Server LocalDB"
            Install-LocalDb
        }
        default { Step "1) لا يلزم SQL على جهاز المستخدم" }
    }
    Step "2) البرنامج"
    $exe = Install-App

    Step "3) التشغيل الأول"
    switch ($Mode) {
        'Server'      { Write-Host "  في معالج الإعداد: السيرفر localhost\SQLEXPRESS، مصادقة Windows، ثم «تثبيت جديد»." }
        'Workstation' { Write-Host "  في معالج الإعداد: اسم جهاز السيرفر (مثل SERVER-PC\SQLEXPRESS) ثم «الاتصال بنظام مثبّت مسبقًا»." }
        'Trainee'     { Write-Host "  يُنشأ تلقائيًا مشروع تدريب ببيانات تجريبية. الدخول: trainee / Trainee@2026" -ForegroundColor Green }
    }
    if (-not $NoLaunch) {
        if ($Mode -eq 'Trainee') { Start-Process $exe -ArgumentList '--trainee' -WorkingDirectory $InstallDir }
        else { Start-Process $exe -WorkingDirectory $InstallDir }
    }
    Write-Host "`nاكتمل التثبيت. السجل: $log" -ForegroundColor Green
}
catch {
    Write-Host "`n[خطأ] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "السجل الكامل: $log"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
