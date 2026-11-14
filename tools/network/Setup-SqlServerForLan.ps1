<#
  تجهيز جهاز السيرفر ليتصل به البرنامج من بقية أجهزة الشبكة.
  يُشغَّل مرة واحدة على جهاز السيرفر فقط، من PowerShell بصلاحية المسؤول (Run as Administrator):

      powershell -ExecutionPolicy Bypass -File .\Setup-SqlServerForLan.ps1

  ما يفعله (وكل خطوة تطبع نتيجتها):
    1. يفعّل بروتوكول TCP/IP لنسخة SQL Server ويثبّت المنفذ 1433.
    2. يفعّل المصادقة المختلطة (Windows + مستخدم SQL).
    3. يشغّل خدمة SQL Server Browser ويجعلها تلقائية.
    4. يفتح المنفذين TCP 1433 وUDP 1434 في جدار الحماية لأجهزة الشبكة المحلية فقط.
    5. يعيد تشغيل خدمة SQL Server (يتوقف النظام القديم والجديد ثوانيَ قليلة).
    6. ينشئ مستخدم SQL للبرنامج (الافتراضي erp_app) بصلاحية على قواعد ERP_* فقط،
       وصلاحية قراءة فقط على قواعد نظام الرحمة القديم (ALRAHMA*) لأداة النقل.

  لا يحذف ولا يعدّل أي بيانات.
#>
param(
    [string]$Instance = 'SQLEXPRESS',
    [string]$AppLogin = 'erp_app',
    [int]$Port = 1433,
    [switch]$SkipLogin
)

$ErrorActionPreference = 'Stop'
function Ok($m)   { Write-Host "  [تم]  $m" -ForegroundColor Green }
function Info($m) { Write-Host "  ...   $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "  [!]   $m" -ForegroundColor Yellow }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'شغّل PowerShell كمسؤول: زر يمين على PowerShell ثم "Run as administrator".' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "تجهيز SQL Server ($env:COMPUTERNAME\$Instance) للعمل على الشبكة" -ForegroundColor White
Write-Host ''

# ── 0) العثور على النسخة ──
$names = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -ErrorAction SilentlyContinue
if (-not $names -or -not $names.$Instance) {
    Write-Host "لم أجد نسخة SQL Server باسم $Instance على هذا الجهاز." -ForegroundColor Red
    if ($names) { Write-Host ("النسخ الموجودة: " + (($names.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }).Name -join ', ')) }
    exit 1
}
$instId = $names.$Instance
$root = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instId\MSSQLServer"
$serviceName = if ($Instance -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { "MSSQL`$$Instance" }
Ok "وُجدت النسخة ($instId)"

# ── 1) TCP/IP + منفذ ثابت ──
$tcp = "$root\SuperSocketNetLib\Tcp"
Set-ItemProperty $tcp -Name Enabled -Value 1
Set-ItemProperty "$tcp\IPAll" -Name TcpDynamicPorts -Value ''
Set-ItemProperty "$tcp\IPAll" -Name TcpPort -Value "$Port"
Ok "تفعيل TCP/IP على المنفذ $Port"

# ── 2) المصادقة المختلطة ──
Set-ItemProperty $root -Name LoginMode -Value 2
Ok 'تفعيل المصادقة المختلطة (Windows + مستخدم SQL)'

# ── 3) خدمة SQL Browser ──
Set-Service SQLBrowser -StartupType Automatic
Start-Service SQLBrowser -ErrorAction SilentlyContinue
Ok 'خدمة SQL Server Browser تعمل وتبدأ تلقائيًا'

# ── 4) جدار الحماية: الشبكة المحلية فقط ──
foreach ($r in @(@{N="ERP SQL Server TCP $Port"; P='TCP'; Port=$Port}, @{N='ERP SQL Browser UDP 1434'; P='UDP'; Port=1434})) {
    Get-NetFirewallRule -DisplayName $r.N -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $r.N -Direction Inbound -Protocol $r.P -LocalPort $r.Port `
        -Action Allow -Profile Any -RemoteAddress LocalSubnet | Out-Null
}
Ok "فتح TCP $Port و UDP 1434 لأجهزة الشبكة المحلية"

# ── 5) إعادة تشغيل SQL Server ──
Info 'إعادة تشغيل خدمة SQL Server (ثوانٍ)...'
Restart-Service $serviceName -Force
Start-Sleep -Seconds 3
Ok 'أُعيد تشغيل SQL Server'

# ── 6) مستخدم البرنامج ──
if (-not $SkipLogin) {
    $pw1 = Read-Host "كلمة مرور جديدة للمستخدم $AppLogin (8 أحرف على الأقل)" -AsSecureString
    $pw2 = Read-Host 'أعد كتابتها' -AsSecureString
    $plain  = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw1))
    $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw2))
    if ($plain -ne $plain2 -or $plain.Length -lt 8) { Write-Host 'كلمتا المرور غير متطابقتين أو أقصر من 8 أحرف. أعد تشغيل السكربت.' -ForegroundColor Red; exit 1 }

    $conn = New-Object System.Data.SqlClient.SqlConnection "Server=.\$Instance;Integrated Security=True;Database=master;Connect Timeout=30"
    $conn.Open()
    function Exec($sql, $params) {
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql
        if ($params) { foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) } }
        [void]$cmd.ExecuteNonQuery()
    }
    $q = $AppLogin.Replace(']', ']]')
    $pwLit = $plain.Replace("'", "''")
    Exec @"
IF SUSER_ID(N'$($AppLogin.Replace("'", "''"))') IS NULL
    CREATE LOGIN [$q] WITH PASSWORD = N'$pwLit', CHECK_POLICY = OFF, DEFAULT_DATABASE = master;
ELSE
    ALTER LOGIN [$q] WITH PASSWORD = N'$pwLit', CHECK_POLICY = OFF;
ALTER LOGIN [$q] ENABLE;
ALTER SERVER ROLE dbcreator ADD MEMBER [$q];
"@ $null
    Ok "المستخدم $AppLogin جاهز (ينشئ مشاريع جديدة عند الحاجة)"

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT name FROM sys.databases WHERE state = 0 AND (name LIKE N'ERP[_]%' OR name LIKE N'ALRAHMA%' OR name = N'msdb')"
    $rd = $cmd.ExecuteReader(); $dbs = @(); while ($rd.Read()) { $dbs += $rd.GetString(0) }; $rd.Close()
    foreach ($db in $dbs) {
        $role = if ($db -like 'ERP_*') { 'db_owner' } else { 'db_datareader' }
        $d = $db.Replace(']', ']]')
        Exec @"
USE [$d];
IF USER_ID(N'$($AppLogin.Replace("'", "''"))') IS NULL CREATE USER [$q] FOR LOGIN [$q];
ALTER ROLE [$role] ADD MEMBER [$q];
"@ $null
        $what = if ($role -eq 'db_owner') { 'كاملة' } else { 'قراءة فقط' }
        Ok "صلاحية $what على $db"
    }
    $conn.Close()
}

# ── الخلاصة ──
$ips = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }).IPAddress
Write-Host ''
Write-Host 'انتهى التجهيز. على كل جهاز في الشبكة اكتب في معالج إعداد البرنامج:' -ForegroundColor White
Write-Host "    اسم السيرفر:  $env:COMPUTERNAME\$Instance" -ForegroundColor Green
foreach ($ip in $ips) { Write-Host "         أو:     $ip\$Instance   (أو $ip,$Port)" -ForegroundColor Green }
if (-not $SkipLogin) { Write-Host "    مستخدم SQL Server:  $AppLogin  وكلمة المرور التي أدخلتها" -ForegroundColor Green }
Write-Host ''
Warn 'اجعل عنوان IP لهذا الجهاز ثابتًا من إعدادات الراوتر، وأوقف وضع السكون (Sleep) فيه.'
