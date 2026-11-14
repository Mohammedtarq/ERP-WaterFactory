# تصدير عينة من قاعدة النظام القديم لدراسة نقل البيانات إلى النظام الجديد.
# يُشغَّل على جهازك فقط، ويقرأ القاعدة دون أي تعديل عليها. الناتج: مجلد ملفات CSV وملف ZIP على سطح المكتب.
# الأسماء والهواتف والعناوين تُخفى افتراضيًا. لإظهارها أضِف -NoMask.
#
# التشغيل (PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\Export-LegacySample.ps1
#   powershell -ExecutionPolicy Bypass -File .\Export-LegacySample.ps1 -Server "localhost\SQLEXPRESS" -Database "اسم_القاعدة"
param(
    [string]$Server = "localhost\SQLEXPRESS",
    [string]$Database,
    [string]$OutDir = (Join-Path ([Environment]::GetFolderPath("Desktop")) "legacy-sample"),
    [switch]$NoMask
)
$ErrorActionPreference = "Stop"

function Open-Connection([string]$db) {
    $cs = "Server=$Server;Database=$db;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15"
    $c = New-Object System.Data.SqlClient.SqlConnection $cs
    $c.Open()
    return $c
}

function Invoke-Query($conn, [string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 600
    $dt = New-Object System.Data.DataTable
    [void](New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($dt)
    return ,$dt
}

if (-not $Database) {
    $m = Open-Connection "master"
    $dbs = Invoke-Query $m "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name"
    $m.Close()
    Write-Host "Databases on $Server :"
    $i = 1; foreach ($r in $dbs.Rows) { Write-Host "  $i) $($r.name)"; $i++ }
    $pick = Read-Host "Type the number of the OLD system database"
    $Database = $dbs.Rows[[int]$pick - 1].name
}

# الإخفاء يتم داخل SQL حتى لا تخرج البيانات الحساسة من السيرفر أصلًا
if ($NoMask) {
    $custName = "customer_name"; $custPhone = "phone"; $custAddr = "address"
    $supName = "name"; $supPhone = "phone"
    $empName = "EmployeeName"; $empPhone = "PhoneNumber"; $empAddr = "address"
} else {
    $custName = "CONCAT('Customer-', id) AS customer_name"; $custPhone = "CASE WHEN phone IS NULL OR phone = '' THEN '' ELSE '***' END AS phone"; $custAddr = "CASE WHEN address IS NULL OR address = '' THEN '' ELSE '***' END AS address"
    $supName = "CONCAT('Supplier-', id) AS name"; $supPhone = "CASE WHEN phone IS NULL OR phone = '' THEN '' ELSE '***' END AS phone"
    $empName = "CONCAT('Employee-', EmployeeID) AS EmployeeName"; $empPhone = "'***' AS PhoneNumber"; $empAddr = "'***' AS address"
}

$queries = [ordered]@{
    # ---- الأصناف والمخازن والوصفات ----
    "01_Products"               = "SELECT * FROM Products"
    "02_RawProdectTable"        = "SELECT * FROM RawProdectTable"
    "03_Warehouses"             = "SELECT 'raw' AS kind, id, wherehouseName FROM RawWherehouseTable UNION ALL SELECT 'entaj', id, wherehouseName FROM EntajWherehouseTable"
    "04_KulafManefactuer_all"   = "SELECT * FROM KulafManefactuerTable ORDER BY proudectid, id"
    "05_RawDetels_latest"       = "SELECT TOP 40 * FROM RawDetelsProudectTable ORDER BY id DESC"
    "06_RawDetels_variants"     = "SELECT rawProudectId, rawProudectname, spichalname, color, rawwherhousename, COUNT(*) AS lots, SUM(number) AS sum_number, SUM(allnumber) AS sum_allnumber, MIN(date) AS first_date, MAX(date) AS last_date, AVG(priceperone) AS avg_price, AVG(usdtoiq) AS avg_usdtoiq FROM RawDetelsProudectTable GROUP BY rawProudectId, rawProudectname, spichalname, color, rawwherhousename ORDER BY rawProudectname, spichalname, color"
    "07_Notificationraw"        = "SELECT TOP 30 * FROM Notificationraw ORDER BY id DESC"
    "08_RawTalf_latest"         = "SELECT TOP 20 * FROM RawTalfTable ORDER BY id DESC"
    "09_RawTalfSell_all"        = "SELECT * FROM RawTalfSellTable ORDER BY id DESC"
    "10_RawFarq_latest"         = "SELECT TOP 30 * FROM RawFarqTable ORDER BY id DESC"
    # ---- الإنتاج ----
    "11_EntajDays_latest"       = "SELECT TOP 20 * FROM EntajDays ORDER BY ENID DESC"
    "12_entajdetels_latest"     = "SELECT TOP 40 * FROM entajdetels ORDER BY id DESC"
    "13_entaj_variants"         = "SELECT prodectid, spechalname, color, COUNT(*) AS rows_count, SUM(entaj) AS sum_entaj, SUM(free) AS sum_free, SUM(mutabaqientaj) AS sum_mutabaqi FROM entajdetels GROUP BY prodectid, spechalname, color ORDER BY prodectid, spechalname, color"
    "14_EntajFromRaw_latest"    = "SELECT TOP 40 * FROM EntajfromrawrelationTable ORDER BY id DESC"
    "15_EntajMashob_latest"     = "SELECT TOP 20 h.*, d.entajdetelsid, d.number AS detail_number FROM EntajMashobTable h LEFT JOIN EntajMashobDetelsTabel d ON d.Mashobentajid = h.id WHERE h.id IN (SELECT TOP 20 id FROM EntajMashobTable ORDER BY id DESC) ORDER BY h.id DESC"
    "16_EntajTalf_latest"       = "SELECT TOP 20 h.*, d.entajdetelsid, d.number AS detail_number FROM EntajTalfTable h LEFT JOIN EntajTalafDetelsTabel d ON d.talafentajid = h.id ORDER BY h.id DESC"
    # ---- المبيعات والعملاء ----
    "17_Fwater_latest"          = "SELECT TOP 25 * FROM Fwater ORDER BY FID DESC"
    "18_Fwaterdetel_latest"     = "SELECT TOP 40 * FROM Fwaterdetel ORDER BY deletid DESC"
    "19_CustomerFwaterHead_latest" = "SELECT TOP 40 * FROM CustomerFwaterHead ORDER BY id DESC"
    "20_CustmerFatwraDetel_latest" = "SELECT TOP 40 * FROM CustmerFatwraDetel ORDER BY id DESC"
    "21_entajselldetels_latest" = "SELECT TOP 30 * FROM entajselldetels ORDER BY id DESC"
    "22_RageTable_latest"       = "SELECT TOP 30 * FROM RageTable ORDER BY id DESC"
    "23_CustomerPay_latest"     = "SELECT TOP 40 * FROM CustomerPay ORDER BY id DESC"
    "24_Customer_sample"        = "SELECT TOP 40 id, $custName, $custPhone, add_at, deon, $custAddr FROM Customer ORDER BY deon DESC"
    "25_Customer_summary"       = "SELECT COUNT(*) AS customers, SUM(deon) AS total_deon, SUM(CASE WHEN deon > 0 THEN 1 ELSE 0 END) AS positive_deon, SUM(CASE WHEN deon < 0 THEN 1 ELSE 0 END) AS negative_deon, SUM(CASE WHEN deon = 0 THEN 1 ELSE 0 END) AS zero_deon FROM Customer"
    "26_Customer_balance_check" = "SELECT TOP 40 c.id, c.deon, (SELECT SUM(allprice) FROM CustomerFwaterHead WHERE CID = c.id) AS inv_allprice, (SELECT SUM(kasem) FROM CustomerFwaterHead WHERE CID = c.id) AS inv_kasem, (SELECT SUM(allprice) FROM CustomerFwaterHead WHERE CID = c.id AND isajel = 1) AS inv_ajel, (SELECT SUM(paidamount) FROM CustomerPay WHERE CustmerID = c.id) AS paid, (SELECT SUM(allamountiraq) FROM EstelamAmanat WHERE cid = c.id) AS amanat_in, (SELECT SUM(amount) FROM TaslemAmanat WHERE cid = c.id) AS amanat_out FROM Customer c ORDER BY c.deon DESC"
    "27_EstelamAmanat_latest"   = "SELECT TOP 20 * FROM EstelamAmanat ORDER BY id DESC"
    "28_TaslemAmanat_all"       = "SELECT * FROM TaslemAmanat ORDER BY id DESC"
    # ---- الصناديق والمصروفات ----
    "29_kasat_all"              = "SELECT * FROM kasat"
    "30_harakatKasa_latest"     = "SELECT TOP 40 * FROM harakatKasa ORDER BY id DESC"
    "31_harakatKasa_types"      = "SELECT TOP 60 type, COUNT(*) AS n FROM harakatKasa GROUP BY type ORDER BY n DESC"
    "32_Boxarchef_latest"       = "SELECT TOP 30 * FROM Boxarchef ORDER BY id DESC"
    "33_MasrofatOffice_latest"  = "SELECT TOP 40 * FROM MasrofatOffice ORDER BY MID DESC"
    "34_Masrofat_types"         = "SELECT typemasrof, COUNT(*) AS n, SUM(amount) AS total FROM MasrofatOffice GROUP BY typemasrof ORDER BY n DESC"
    "35_MasrofatOfficeout_all"  = "SELECT * FROM MasrofatOfficeout ORDER BY MID DESC"
    "36_Eradatoffice_latest"    = "SELECT TOP 30 * FROM Eradatoffice ORDER BY EID DESC"
    "37_Partnerprofits_latest"  = "SELECT TOP 30 * FROM Partnerprofits ORDER BY id DESC"
    "38_AsarSarf_all"           = "SELECT * FROM AsarSarf ORDER BY date"
    # ---- الموردون والمشتريات ----
    "39_Supplier_all"           = "SELECT id, $supName, $supPhone, deon FROM Supplier"
    "40_SupplierDetele_latest"  = "SELECT TOP 40 * FROM SupplierDetele ORDER BY id DESC"
    "41_SupplierPay_latest"     = "SELECT TOP 30 * FROM SupplierPay ORDER BY id DESC"
    "42_BuyRequest_latest"      = "SELECT TOP 40 d.*, h.titel, h.datetime FROM BuyRequestDetelsTable d JOIN BuyRequestTable h ON h.id = d.Buy_id ORDER BY d.id DESC"
    # ---- الموظفون والمستخدمون ----
    "43_Employees_sample"       = "SELECT TOP 15 EmployeeID, $empName, $empPhone, SalaryCurrency, NominalSalary, ShiftID, JobTitleID, DepartmentID, isWorke, DateStartWork, AllowanceAmount, AllowanceType FROM Employees ORDER BY EmployeeID DESC"
    "44_JobTitles_Departments"  = "SELECT 'job' AS kind, JobTitleID AS id, JobTitleName AS name, isworker FROM JobTitles UNION ALL SELECT 'dept', DepartmentID, DepartmentName, NULL FROM Departments"
    "45_Rwateb_latest"          = "SELECT TOP 15 * FROM Rwateb ORDER BY SID DESC"
    "46_Slaf_Mashobat_latest"   = "SELECT TOP 15 'slaf' AS kind, SlafID AS id, EID, date, amount FROM SlafTabel UNION ALL SELECT TOP 15 'mashobat', MasID, EID, date, amount FROM Mashobat ORDER BY date DESC"
    "47_Users_no_passwords"     = "SELECT u.UserID, u.name, u.username, g.groupname FROM Users u LEFT JOIN UserGroup g ON g.user_id = u.UserID"
    "48_Information"            = "SELECT inf_id, name, phone, address FROM Information"
    # ---- مدى التاريخ في كل جدول رئيسي ----
    "49_Date_ranges"            = "SELECT 'Fwater' AS t, MIN(date) AS first_date, MAX(date) AS last_date FROM Fwater UNION ALL SELECT 'CustomerFwaterHead', MIN(date), MAX(date) FROM CustomerFwaterHead UNION ALL SELECT 'CustomerPay', MIN(date), MAX(date) FROM CustomerPay UNION ALL SELECT 'EntajDays', MIN(datetime), MAX(datetime) FROM EntajDays UNION ALL SELECT 'RawDetels', MIN(date), MAX(date) FROM RawDetelsProudectTable UNION ALL SELECT 'MasrofatOffice', MIN(date), MAX(date) FROM MasrofatOffice UNION ALL SELECT 'harakatKasa', MIN(datetime), MAX(datetime) FROM harakatKasa"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$conn = Open-Connection $Database
Write-Host "Exporting from [$Database] to $OutDir"
$failed = @()
foreach ($name in $queries.Keys) {
    try {
        $dt = Invoke-Query $conn $queries[$name]
        $dt | Select-Object * -ExcludeProperty RowError, RowState, Table, ItemArray, HasErrors |
            Export-Csv -Path (Join-Path $OutDir "$name.csv") -NoTypeInformation -Encoding UTF8
        Write-Host ("  {0,-32} {1,6} rows" -f $name, $dt.Rows.Count)
    } catch {
        $failed += $name
        Set-Content -Path (Join-Path $OutDir "$name.ERROR.txt") -Value $_.Exception.Message -Encoding UTF8
        Write-Host "  $name  FAILED: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
$conn.Close()

$zip = "$OutDir.zip"
Compress-Archive -Path (Join-Path $OutDir "*") -DestinationPath $zip -Force
Write-Host ""
Write-Host "Done. Send this file: $zip" -ForegroundColor Green
if ($failed.Count -gt 0) { Write-Host "Some queries failed (their .ERROR.txt files are included): $($failed -join ', ')" -ForegroundColor Yellow }
