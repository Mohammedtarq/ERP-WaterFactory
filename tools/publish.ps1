# يُنتج حزمة تشغيل مستقلة لـ Windows (لا تحتاج تثبيت .NET على جهاز العميل)
# الاستخدام (من PowerShell في مجلد المشروع):  .\tools\publish.ps1
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$out = "dist\ERP-WaterFactory-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish ERP.Desktop\ERP.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $out
# خدمة المزامنة السحابية (ملف واحد) لجهاز السيرفر — تُثبَّت يدويًا كمسؤول عند تفعيل تطبيق المندوبين
dotnet publish ERP.SyncAgent\ERP.SyncAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$out\SyncAgent"
if ($LASTEXITCODE) { throw "تعذّر نشر خدمة المزامنة السحابية" }
Copy-Item docs\INSTALL.md "$out\اقرأني - التثبيت.md" -ErrorAction SilentlyContinue
New-Item "$out\الشبكة" -ItemType Directory -Force | Out-Null
Copy-Item docs\network-setup.md "$out\الشبكة\دليل تجهيز الشبكة.md" -ErrorAction SilentlyContinue
Copy-Item tools\network\Setup-SqlServerForLan.ps1 "$out\الشبكة\" -ErrorAction SilentlyContinue
# المثبّت: سيرفر (مع SQL Server Express) أو جهاز مستخدم أو جهاز متدرب (مع LocalDB وقاعدة تدريب)
Copy-Item tools\installer\* $out -ErrorAction SilentlyContinue
Write-Host "الحزمة جاهزة: $out" -ForegroundColor Green
