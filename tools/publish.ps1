# يُنتج حزمة تشغيل مستقلة لـ Windows (لا تحتاج تثبيت .NET على جهاز العميل)
# الاستخدام (من PowerShell في مجلد المشروع):  .\tools\publish.ps1
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$out = "dist\ERP-WaterFactory-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish ERP.Desktop\ERP.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $out
# خدمة المزامنة (ملف واحد) لحاسبة السيرفر — يثبّتها المثبّت بوضع «السيرفر» تلقائيًا
dotnet publish ERP.SyncAgent\ERP.SyncAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$out\SyncAgent"
if ($LASTEXITCODE) { throw "تعذّر نشر خدمة المزامنة" }
# الخادم المحلي (واي فاي المعمل): خدمة Windows يثبّتها المثبّت بوضع «السيرفر»
dotnet publish ERP.Cloud.Api\ERP.Cloud.Api.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$out\LocalServer"
if ($LASTEXITCODE) { throw "تعذّر نشر الخادم المحلي" }
# الخادم السحابي: النسخة نفسها لأي نظام (تعمل في Docker)، يضعها البرنامج في «ملفات نشر الخادم السحابي»
dotnet publish ERP.Cloud.Api\ERP.Cloud.Api.csproj -c Release -p:DebugType=none -p:UseAppHost=false -o "$out\CloudServer"
if ($LASTEXITCODE) { throw "تعذّر نشر الخادم السحابي" }
Copy-Item docs\rep-app.md "$out\اقرأني - تطبيق المندوب.md" -ErrorAction SilentlyContinue
# شعار المعمل جاهزًا للرفع في «الإعدادات ← هوية الطباعة»
New-Item "$out\الشعار" -ItemType Directory -Force | Out-Null
Copy-Item docs\brand\* "$out\الشعار\" -ErrorAction SilentlyContinue
Copy-Item docs\INSTALL.md "$out\اقرأني - التثبيت.md" -ErrorAction SilentlyContinue
New-Item "$out\الشبكة" -ItemType Directory -Force | Out-Null
Copy-Item docs\network-setup.md "$out\الشبكة\دليل تجهيز الشبكة.md" -ErrorAction SilentlyContinue
Copy-Item tools\network\Setup-SqlServerForLan.ps1 "$out\الشبكة\" -ErrorAction SilentlyContinue
# المثبّت: سيرفر (مع SQL Server Express) أو جهاز مستخدم أو جهاز متدرب (مع LocalDB وقاعدة تدريب)
Copy-Item tools\installer\* $out -ErrorAction SilentlyContinue
Write-Host "الحزمة جاهزة: $out" -ForegroundColor Green
