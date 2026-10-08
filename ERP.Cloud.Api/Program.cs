using ERP.Cloud.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var relay = builder.Configuration.GetConnectionString("Relay");
if (string.IsNullOrWhiteSpace(relay))
    throw new InvalidOperationException("سلسلة اتصال القاعدة الوسيطة غير مضبوطة (ConnectionStrings:Relay)");
builder.Services.AddDbContext<RelayDb>(o => o.UseSqlServer(relay, s => s.EnableRetryOnFailure(3)));
// صورة الوصل (3MB) بترميز base64 داخل JSON
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 8 * 1024 * 1024);

var app = builder.Build();

// القاعدة الوسيطة تُنشأ تلقائيًا في أول تشغيل (لا ترقيات يدوية)
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<RelayDb>().Database.EnsureCreatedAsync();

RelayEndpoints.Map(app);
app.Run();

public partial class Program { }
