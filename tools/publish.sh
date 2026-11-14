#!/usr/bin/env bash
# يُنتج حزمة تشغيل مستقلة لـ Windows (لا تحتاج تثبيت .NET على جهاز العميل):
#   dist/ERP-WaterFactory-win-x64/ERP.Desktop.exe
# الاستخدام: ./tools/publish.sh   (يعمل على Windows أو Linux أو macOS مع .NET 9 SDK)
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=dist/ERP-WaterFactory-win-x64
rm -rf "$OUT"
dotnet publish ERP.Desktop/ERP.Desktop.csproj -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:NuGetAudit=false -p:DebugType=none -o "$OUT"
cp docs/INSTALL.md "$OUT/اقرأني - التثبيت.md" 2>/dev/null || true
echo "✅ الحزمة جاهزة: $OUT"
