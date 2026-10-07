# تجربة تطبيق المندوب عبر واي فاي المعمل (قبل الخادم السحابي):
# يشغّل الخادم الوسيط على جهاز السيرفر نفسه، ثم خدمة المزامنة في هذه النافذة. أغلق النافذة لإيقاف التجربة.
# التفاصيل: docs/rep-app.md
param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$server = Join-Path $here 'CloudServer\ERP.Cloud.Api.exe'
$agent = Join-Path (Split-Path $here -Parent) 'SyncAgent\ERP.SyncAgent.exe'

$keyFile = Join-Path $here 'agent-key.txt'
if (-not (Test-Path $keyFile) -or -not ([string](Get-Content $keyFile -Raw)).Trim()) {
    Set-Content -Path $keyFile -Value '' -Encoding ASCII
    Write-Host 'الصق «مفتاح المعمل» من شاشة «الإعدادات ← المزامنة السحابية» في الملف الذي سيُفتح، واحفظه، ثم أعد التشغيل.' -ForegroundColor Yellow
    notepad $keyFile
    exit 1
}
$env:Relay__AgentKey = ([string](Get-Content $keyFile -Raw)).Trim()
$env:ConnectionStrings__Relay = 'Server=localhost\SQLEXPRESS;Database=ERP_Relay;Integrated Security=True;TrustServerCertificate=True'

$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
       Where-Object { $_.IPAddress -match '^(192\.168\.|10\.|172\.(1[6-9]|2\d|3[01])\.)' } | Select-Object -First 1).IPAddress
Write-Host ''
Write-Host "عنوان الخادم للتجربة: http://${ip}:$Port" -ForegroundColor Green
Write-Host 'اكتبه في «المزامنة السحابية» (وفعّلها)، ثم سجّل هواتف المندوبين من «أجهزة التطبيق».' -ForegroundColor Green
Write-Host ''

$proc = Start-Process -FilePath $server -ArgumentList "--urls http://0.0.0.0:$Port" -PassThru -WindowStyle Minimized
try {
    Start-Sleep -Seconds 4
    & $agent
}
finally {
    Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
}
