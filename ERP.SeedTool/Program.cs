using ERP.Data.Setup;

// ============================================================
// أداة تجهيز من سطر الأوامر — نفس ما يفعله معالج الإعداد داخل البرنامج
// (مفيدة للتجهيز على سيرفر بدون واجهة، أو لإعادة تجهيز بيئة تجربة).
//
// الاستخدام:
//   dotnet run --project ERP.SeedTool
//   dotnet run --project ERP.SeedTool -- "<سلسلة اتصال قاعدة التحكم>" [--no-demo]
//
// آمنة للتكرار: على نظام قائم تُكمل الناقص وترقّي المخطط فقط ولا تكرر شيئًا.
// ============================================================

var controlCs = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? "Server=localhost;Database=ERP_ControlDB;Trusted_Connection=True;TrustServerCertificate=True;";
bool demo = !args.Contains("--no-demo");

const string username = "admin";
const string password = "Admin@123";

var result = await new ProvisioningService().InstallAsync(
    new InstallRequest(controlCs, "مصنع المياه - البصرة", "ERP_Project_WaterFactory", "مدير النظام", username, password, demo),
    new Progress<string>(Console.WriteLine));

// Progress<T> يُبلّغ على مجمّع الخيوط؛ مهلة قصيرة حتى تُطبع آخر الرسائل
await Task.Delay(200);

if (!result.Success)
{
    Console.WriteLine();
    Console.WriteLine("فشل التجهيز: " + result.ErrorMessage);
    return 1;
}

Console.WriteLine();
Console.WriteLine("=== جاهز للتجربة ===");
Console.WriteLine($"اسم المستخدم: {username}");
Console.WriteLine($"كلمة المرور:  {password}");
Console.WriteLine("(إن كان المستخدم موجودًا مسبقًا تبقى كلمة مروره كما هي)");
return 0;
