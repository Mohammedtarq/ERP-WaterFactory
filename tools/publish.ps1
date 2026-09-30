# يُنتج حزمة تشغيل مستقلة لـ Windows (لا تحتاج تثبيت .NET على جهاز العميل)
# الاستخدام (من PowerShell في مجلد المشروع):  .\tools\publish.ps1
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$out = "dist\ERP-WaterFactory-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish ERP.Desktop\ERP.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $out
Copy-Item docs\INSTALL.md "$out\اقرأني - التثبيت.md" -ErrorAction SilentlyContinue
Write-Host "الحزمة جاهزة: $out" -ForegroundColor Green
