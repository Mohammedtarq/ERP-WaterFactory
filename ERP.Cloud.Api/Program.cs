using ERP.Cloud.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// خدمة Windows على حاسبة المعمل (الخادم المحلي)؛ لا أثر لها خارج Windows (الاستضافة السحابية)
builder.Host.UseWindowsService(o => o.ServiceName = "ERPLocalRelay");
// إعداد الخادم المحلي يكتبه المثبّت هنا (القاعدة وملف المفتاح والمنافذ) — لا تعديل يدوي
if (OperatingSystem.IsWindows())
    builder.Configuration.AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                                   "ERP-WaterFactory", "LocalServer", "relay.json"), optional: true);
if (builder.Configuration["Relay:Listen"] is { Length: > 0 } listen) builder.WebHost.UseUrls(listen);

// القاعدة: ملف SQLite (الخادم المحلي والاستضافة الصغيرة) أو SQL Server
var sqlite = builder.Configuration["Relay:SqlitePath"];
var relay = builder.Configuration.GetConnectionString("Relay");
if (!string.IsNullOrWhiteSpace(sqlite))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sqlite))!);
    builder.Services.AddDbContext<RelayDb>(o => o.UseSqlite($"Data Source={sqlite};Default Timeout=30"));
}
else if (!string.IsNullOrWhiteSpace(relay))
    builder.Services.AddDbContext<RelayDb>(o => o.UseSqlServer(relay, s => s.EnableRetryOnFailure(3)));
else
    throw new InvalidOperationException("قاعدة الخادم غير مضبوطة: Relay:SqlitePath أو ConnectionStrings:Relay");

builder.Services.AddSingleton<AgentKeyProvider>();
builder.Services.AddHostedService<DiscoveryResponder>();
// صورة الوصل (3MB) بترميز base64 داخل JSON
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

var app = builder.Build();

// القاعدة الوسيطة تُنشأ تلقائيًا في أول تشغيل (لا ترقيات يدوية)
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<RelayDb>().PrepareAsync();

RelayEndpoints.Map(app);
app.Run();

public partial class Program { }
