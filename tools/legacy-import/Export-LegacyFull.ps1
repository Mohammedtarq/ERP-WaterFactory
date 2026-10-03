# تصدير كامل لقاعدة النظام القديم (كل الجداول بكل صفوفها) لدراسة النظام بعمق قبل خطة التكامل.
# يُشغَّل على جهاز السيرفر، ويقرأ القاعدة فقط دون أي تعديل عليها.
# الناتج: ملف ZIP على سطح المكتب يحوي:
#   - ملف CSV لكل جدول (كل الصفوف)
#   - schema.csv: الجداول والأعمدة وأنواعها، و relations.csv: العلاقات بين الجداول
#   - sql-logic: نصوص الإجراءات المخزّنة والمشغّلات وطرق العرض (منطق الحساب داخل القاعدة)
#   - manifest.csv: عدد صفوف كل جدول
# لا يُصدَّر: الصور والملفات الثنائية، كلمات المرور، الرسائل الداخلية بين المستخدمين.
#
# التشغيل (PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\Export-LegacyFull.ps1
#   powershell -ExecutionPolicy Bypass -File .\Export-LegacyFull.ps1 -Server "localhost\SQLEXPRESS" -Database "ALRAHMA"
param(
    [string]$Server = "localhost\SQLEXPRESS",
    [string]$Database,
    [string]$OutDir = (Join-Path ([Environment]::GetFolderPath("Desktop")) "legacy-full")
)
$ErrorActionPreference = "Stop"
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Open-Connection([string]$db) {
    $cs = "Server=$Server;Database=$db;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15;Application Intent=ReadOnly"
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

function Format-Cell($v) {
    if ($v -is [System.DBNull] -or $null -eq $v) { return "" }
    if ($v -is [datetime]) { return $v.ToString("yyyy-MM-dd HH:mm:ss", $inv) }
    if ($v -is [timespan]) { return $v.ToString("c", $inv) }
    if ($v -is [bool]) { if ($v) { return "1" } else { return "0" } }
    if ($v -is [double] -or $v -is [single] -or $v -is [decimal]) { return ([System.IConvertible]$v).ToString($inv) }
    $s = [string]$v
    if ($s.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) { return '"' + $s.Replace('"', '""') + '"' }
    return $s
}

# يكتب نتيجة استعلام مباشرة إلى CSV صفًا صفًا (يتحمّل الجداول الكبيرة دون استهلاك الذاكرة)
function Export-Reader($conn, [string]$sql, [string]$path) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 0
    $reader = $cmd.ExecuteReader()
    $writer = New-Object System.IO.StreamWriter($path, $false, (New-Object System.Text.UTF8Encoding($true)))
    try {
        $names = @(); for ($i = 0; $i -lt $reader.FieldCount; $i++) { $names += (Format-Cell $reader.GetName($i)) }
        $writer.WriteLine(($names -join ","))
        $rows = 0
        $values = New-Object object[] $reader.FieldCount
        while ($reader.Read()) {
            [void]$reader.GetValues($values)
            $cells = New-Object string[] $values.Length
            for ($i = 0; $i -lt $values.Length; $i++) { $cells[$i] = Format-Cell $values[$i] }
            $writer.WriteLine(($cells -join ","))
            $rows++
        }
        return $rows
    } finally {
        $writer.Close()
        $reader.Close()
    }
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

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "tables") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir "sql-logic") | Out-Null
$conn = Open-Connection $Database
Write-Host "Exporting ALL tables from [$Database] to $OutDir"

# ---- الهيكل والعلاقات ----
$schemaSql = @"
SELECT t.name AS table_name, c.column_id, c.name AS column_name, ty.name AS data_type, c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity,
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS is_pk,
       dc.definition AS default_value
FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
ORDER BY t.name, c.column_id
"@
[void](Export-Reader $conn $schemaSql (Join-Path $OutDir "schema.csv"))
$relSql = @"
SELECT fk.name AS fk_name, tp.name AS from_table, cp.name AS from_column, tr.name AS to_table, cr.name AS to_column
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
JOIN sys.tables tr ON tr.object_id = fkc.referenced_object_id JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
ORDER BY tp.name
"@
[void](Export-Reader $conn $relSql (Join-Path $OutDir "relations.csv"))

# ---- منطق الحساب داخل القاعدة: إجراءات، مشغّلات، طرق عرض، دوال ----
$modules = Invoke-Query $conn "SELECT o.name, o.type_desc, m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0"
foreach ($r in $modules.Rows) {
    $safe = ($r.name -replace '[\\/:*?"<>|]', '_')
    Set-Content -Path (Join-Path $OutDir "sql-logic\$($r.type_desc)_$safe.sql") -Value $r.definition -Encoding UTF8
}
Write-Host ("  SQL logic objects: {0}" -f $modules.Rows.Count)

# ---- كل الجداول بكل صفوفها (عدا الأعمدة الثنائية وكلمات المرور والرسائل الداخلية) ----
$skipTables = @('MailSendTable', 'MailtoTable', 'MailFileTable', 'sysdiagrams')
$skipTypes = @('image', 'varbinary', 'binary', 'timestamp', 'rowversion', 'geography', 'geometry', 'hierarchyid', 'sql_variant', 'xml')
$skipNames = @('pass', 'password', 'pwd', 'passwordhash')
$cols = Invoke-Query $conn "SELECT s.name AS sch, t.name AS tbl, c.name AS col, ty.name AS typ, c.column_id FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id ORDER BY t.name, c.column_id"
$manifest = New-Object System.Collections.Generic.List[string]
$manifest.Add("table,rows,skipped_columns,status")
$failed = @()
$tables = $cols.Rows | Group-Object { "$($_.sch)|$($_.tbl)" }
foreach ($g in $tables) {
    $sch = $g.Group[0].sch; $tbl = $g.Group[0].tbl
    if ($skipTables -contains $tbl) { $manifest.Add("$tbl,,,skipped (internal messages)"); continue }
    $keep = @(); $skipped = @()
    foreach ($c in $g.Group) {
        if (($skipTypes -contains $c.typ) -or ($skipNames -contains ([string]$c.col).ToLowerInvariant())) { $skipped += $c.col } else { $keep += "[" + ([string]$c.col).Replace("]", "]]") + "]" }
    }
    if ($keep.Count -eq 0) { $manifest.Add("$tbl,,$($skipped -join ' '),no exportable columns"); continue }
    $sql = "SELECT " + ($keep -join ", ") + " FROM [" + $sch.Replace("]", "]]") + "].[" + $tbl.Replace("]", "]]") + "]"
    try {
        $n = Export-Reader $conn $sql (Join-Path $OutDir "tables\$tbl.csv")
        $manifest.Add("$tbl,$n,$($skipped -join ' '),ok")
        Write-Host ("  {0,-34} {1,8} rows" -f $tbl, $n)
    } catch {
        $failed += $tbl
        $manifest.Add("$tbl,,,FAILED: $($_.Exception.Message -replace ',', ';')")
        Write-Host "  $tbl  FAILED: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
Set-Content -Path (Join-Path $OutDir "manifest.csv") -Value $manifest -Encoding UTF8
$conn.Close()

$zip = "$OutDir.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $OutDir "*") -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Done. Send this file: $zip  ($mb MB)" -ForegroundColor Green
if ($failed.Count -gt 0) { Write-Host "Some tables failed (see manifest.csv): $($failed -join ', ')" -ForegroundColor Yellow }
